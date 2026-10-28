using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using ScreenStocksBridge;

public static class OwnedRequestRegression
{
    private sealed class Routine : IEnumerator, IDisposable
    {
        internal int Moves, Disposals;
        internal Action? DuringMove;
        internal bool Throw;
        public object Current => null!;
        public bool MoveNext() { Moves++; DuringMove?.Invoke(); if (Throw) throw new InvalidOperationException("Synthetic coroutine failure"); return true; }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }

    public static void Run(Assembly game)
    {
        var owner = new object(); var source = new TaskCompletionSource<bool>(); var routine = new Routine(); var aborted = 0;
        var request = OwnedApiRequests.Track(owner, routine, source.Task, (_, __) => source.TrySetResult(false), () => aborted++);
        Check(request.MoveNext(), "started request yields");
        Check(OwnedApiRequests.Cancel(owner), "owned request can be stopped");
        Check(source.Task.IsCompleted && !source.Task.Result && aborted == 1 && routine.Disposals == 1, "abort, dispose and settle once");
        Check(!request.MoveNext() && routine.Moves == 1 && !source.TrySetResult(true), "late iterator cannot overwrite result or resume HTTP");
        request.Dispose(); Check(aborted == 1 && routine.Disposals == 1, "dispose is idempotent");

        var deferred = new TaskCompletionSource<bool>(); var nested = new Routine(); var inMove = false; var deferredAbort = false;
        nested.DuringMove = () => { inMove = true; OwnedApiRequests.Cancel(nested); Check(!deferredAbort, "defer abort during native advancement"); inMove = false; };
        var deferredRequest = OwnedApiRequests.Track(nested, nested, deferred.Task, (_, __) => deferred.TrySetResult(false),
            () => { Check(!inMove, "abort after native advancement exits"); deferredAbort = true; });
        Check(!deferredRequest.MoveNext() && deferredAbort && deferred.Task.IsCompleted, "deferred cancellation completes before yielding");

        var broken = new TaskCompletionSource<bool>(); var brokenOwner = new object();
        OwnedApiRequests.Track(brokenOwner, new Routine(), broken.Task, (_, __) => broken.TrySetResult(false), () => throw new Exception("Synthetic abort failure"));
        Check(!OwnedApiRequests.Cancel(brokenOwner) && broken.Task.IsCompleted, "abort failure settles task but is retained as a blocking failure");

        var throwing = new TaskCompletionSource<bool>(); var throwingOwner = new object();
        var throwingRequest = OwnedApiRequests.Track(throwingOwner, new Routine { Throw = true }, throwing.Task, (_, __) => throwing.TrySetResult(false), () => { });
        Check(!throwingRequest.MoveNext() && throwing.Task.IsCompleted, "coroutine exception cannot strand its source task");

        var old = new TaskCompletionSource<bool>(); var other = new TaskCompletionSource<bool>(); var sharedApi = new object();
        OwnedApiRequests.Track(sharedApi, new Routine(), old.Task, (_, __) => old.TrySetResult(false), () => { }, principal: "old");
        OwnedApiRequests.Track(sharedApi, new Routine(), other.Task, (_, __) => other.TrySetResult(false), () => { }, principal: "new");
        OwnedApiRequests.Cancel(sharedApi, "old");
        Check(old.Task.IsCompleted && !other.Task.IsCompleted, "old principal cancellation does not touch new principal");
        OwnedApiRequests.Cancel(sharedApi, "new");

        VerifyRealCoroutineAdapter(game);
        Console.WriteLine("PASS: owned request lifecycle stops late resumes, settles failures, defers reentrant aborts, and isolates principals");
    }

    private static void VerifyRealCoroutineAdapter(Assembly game)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var apiType = game.GetType("ScreenStocks.Net.ScreenStocksApiClient", true);
        var api = FormatterServices.GetUninitializedObject(apiType);
        var sessionType = game.GetType("ScreenStocks.Net.AuthSession", true);
        var session = FormatterServices.GetUninitializedObject(sessionType);
        sessionType.GetField("<SteamId>k__BackingField", flags).SetValue(session, "adapter-principal");
        apiType.GetField("Session", flags).SetValue(api, session);
        var dto = game.GetType("ScreenStocks.Net.WirePlayerServerState", true);
        var resultType = game.GetType("ScreenStocks.Net.ApiResult`1", true).MakeGenericType(dto);
        var sourceType = typeof(TaskCompletionSource<>).MakeGenericType(resultType);
        var source = Activator.CreateInstance(sourceType);
        var iterator = (IEnumerator)apiType.GetMethod("SendCoroutine", flags).MakeGenericMethod(dto)
            .Invoke(api, new object?[] { "GET", "/state", null, true, source });
        var wrapped = OwnedApiRequests.Wrap(iterator);
        Check(!ReferenceEquals(iterator, wrapped), "actual generic API iterator is wrapped without generic detours");
        OwnedApiRequests.Cancel(api, "adapter-principal");
        var task = (Task)sourceType.GetProperty("Task").GetValue(source);
        Check(task.Status == TaskStatus.RanToCompletion && !wrapped.MoveNext(), "actual API completion source settled before native request construction");
        var result = task.GetType().GetProperty("Result").GetValue(task);
        Check((string)resultType.GetField("Error").GetValue(result) == "BridgeRecoveryInterrupted", "native result uses explicit interruption reason");
    }

    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("Owned request assertion failed: " + label); }
}
