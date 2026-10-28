using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using ScreenStocks.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace ScreenStocksBridge
{
    internal static class OwnedApiRequests
    {
        private static readonly object Gate = new object();
        private static readonly List<OwnedApiRequest> Active = new List<OwnedApiRequest>();
        private static readonly Dictionary<object, StateRead> Reads = new Dictionary<object, StateRead>();
        private static readonly HashSet<object> AbortFailures = new HashSet<object>();
        private static long _sequence;
        internal static Action<string>? Warning;
        internal sealed class StateRead
        {
            internal long Sequence;
            internal WirePlayerServerState State = null!;
        }

        internal static void Install(Harmony harmony)
        {
            // Hook the non-generic coroutine entry point. Patching constructed
            // generic API methods would risk Mono's shared generic method bodies.
            var start = typeof(MonoBehaviour).GetMethod("StartCoroutine", new[] { typeof(IEnumerator) })
                ?? throw new MissingMethodException("MonoBehaviour.StartCoroutine(IEnumerator)");
            harmony.Patch(start, prefix: new HarmonyMethod(typeof(OwnedApiRequests), nameof(StartPrefix)));
        }

        private static void StartPrefix(ref IEnumerator __0) => __0 = Wrap(__0);

        internal static IEnumerator Wrap(IEnumerator routine)
        {
            var type = routine.GetType();
            if (type.DeclaringType != typeof(ScreenStocksApiClient) ||
                !type.Name.StartsWith("<SendCoroutine>", StringComparison.Ordinal)) return routine;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var owner = type.GetField("<>4__this", flags)!.GetValue(routine);
            var source = type.GetField("tcs", flags)!.GetValue(routine);
            var path = (string)type.GetField("path", flags)!.GetValue(routine);
            var method = (string)type.GetField("method", flags)!.GetValue(routine);
            var requestField = type.GetField("<req>5__3", flags)
                ?? throw new MissingFieldException(type.FullName, "<req>5__3");
            var task = (Task)source.GetType().GetProperty("Task")!.GetValue(source);
            var resultType = source.GetType().GetGenericArguments()[0];
            var fail = resultType.GetMethod("Fail", BindingFlags.Static | BindingFlags.Public)!;
            var complete = source.GetType().GetMethod("TrySetResult")!;
            return Track(owner, routine, task,
                (code, reason) => complete.Invoke(source, new[] { fail.Invoke(null, new object?[] { code, reason, null, 0 }) }),
                () =>
                {
                    var request = requestField.GetValue(routine) as UnityWebRequest;
                    if (request == null) return;
                    try { request.Abort(); }
                    finally { request.Dispose(); }
                },
                () =>
                {
                    if (method != "GET" || path != "/state" || task.Status != TaskStatus.RanToCompletion) return;
                    var result = task.GetType().GetProperty("Result")!.GetValue(task);
                    if (!(bool)resultType.GetField("Success")!.GetValue(result)) return;
                    if (!(resultType.GetField("Data")!.GetValue(result) is WirePlayerServerState state)) return;
                    lock (Gate) Reads[owner] = new StateRead { Sequence = ++_sequence, State = state };
                }, IsMutation(method, path), ((ScreenStocksApiClient)owner).Session.SteamId);
        }

        internal static bool IsMutation(string method, string path) => method != "GET" &&
            !path.StartsWith("/auth/", StringComparison.Ordinal) && !path.StartsWith("/admission/", StringComparison.Ordinal);

        internal static OwnedApiRequest Track(object owner, IEnumerator routine, Task task,
            Action<int, string> settle, Action abort, Action? read = null, bool mutation = false, string? principal = null)
        {
            var request = new OwnedApiRequest(owner, routine, task, settle, abort, read, mutation, principal);
            lock (Gate) Active.Add(request);
            return request;
        }

        internal static StateRead? LatestRead(object owner) { lock (Gate) return Reads.TryGetValue(owner, out var read) ? read : null; }
        internal static bool HadAbortFailure(object owner) { lock (Gate) return AbortFailures.Contains(owner); }
        internal static bool Cancel(object owner, string? principal = null)
        {
            OwnedApiRequest[] requests;
            lock (Gate) requests = Active.Where(r => ReferenceEquals(r.Owner, owner) && !r.Task.IsCompleted &&
                (principal == null || r.Principal == principal)).ToArray();
            foreach (var request in requests) request.Stop(0, "BridgeRecoveryInterrupted");
            return !HadAbortFailure(owner);
        }
        private static void Remove(OwnedApiRequest request) { lock (Gate) Active.Remove(request); }

        internal sealed class OwnedApiRequest : IEnumerator, IDisposable
        {
            internal object Owner { get; }
            internal string? Principal { get; }
            internal Task Task { get; }
            private readonly IEnumerator _inner;
            private readonly Action<int, string> _settle;
            private readonly Action _abort;
            private readonly Action? _read;
            private readonly bool _mutation;
            private bool _moving, _stopped, _finished;
            private int _code;
            private string _reason = "BridgeRecoveryInterrupted";

            internal OwnedApiRequest(object owner, IEnumerator inner, Task task, Action<int, string> settle,
                Action abort, Action? read, bool mutation, string? principal)
            { Owner = owner; Principal = principal; _inner = inner; Task = task; _settle = settle; _abort = abort; _read = read; _mutation = mutation; }

            public object Current => _inner.Current;
            public void Reset() => throw new NotSupportedException();
            public bool MoveNext()
            {
                if (_stopped) { FinishStop(); return false; }
                if (_mutation && TradeRecoveryService.BlocksCommands)
                { Stop(409, "BridgeRecoveryInProgress"); return false; }
                bool more;
                _moving = true;
                try { more = _inner.MoveNext(); }
                catch (Exception ex)
                {
                    Warning?.Invoke("Game API coroutine exited with " + ex.GetType().Name + "; its waiter was completed.");
                    _stopped = true;
                    more = false;
                }
                finally { _moving = false; }
                if (_stopped) { FinishStop(); return false; }
                if (Task.IsCompleted)
                {
                    try { _read?.Invoke(); }
                    finally { _finished = true; Remove(this); }
                }
                else if (!more) Stop(0, "BridgeRequestEndedWithoutResult");
                return more;
            }

            internal void Stop(int code, string reason)
            {
                if (_finished) return;
                _stopped = true; _code = code; _reason = reason;
                if (!_moving) FinishStop();
            }

            private void FinishStop()
            {
                if (_finished || _moving) return;
                _finished = true;
                try
                {
                    if (!Task.IsCompleted) _abort();
                    (_inner as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    lock (Gate) AbortFailures.Add(Owner);
                    Warning?.Invoke("Could not stop an owned API request: " + ex.GetType().Name);
                }
                finally
                {
                    // Even Abort/Dispose failures must not strand the Task. The
                    // coordinator separately fails closed on an abort failure.
                    try { if (!Task.IsCompleted) _settle(_code, _reason); }
                    finally { Remove(this); }
                }
            }

            public void Dispose() { if (!_finished) Stop(0, "BridgeRequestDisposed"); }
        }
    }
}
