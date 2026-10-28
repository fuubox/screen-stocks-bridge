using System;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScreenStocksBridge;

// Exercise native recovery/reset/gate code, replacing only the JSON request
// task with a controlled response. Never enter Unity's HTTP/coroutine layer.
public static class SessionRecoveryRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Assembly _game = null!;
    private static Type _apiType = null!;
    private static Type _brokerType = null!;
    private static Type _responseType = null!;
    private static Task _transport = null!;
    private static int _requests;
    private static int _notifications;

    public static void Run(Assembly game)
    {
        _game = game;
        _apiType = game.GetType("ScreenStocks.Net.ScreenStocksApiClient", true);
        _brokerType = game.GetType("RemoteTradeBroker", true);
        var dto = game.GetType("ScreenStocks.Net.TradeResultDto", true);
        _responseType = game.GetType("ScreenStocks.Net.ApiResult`1", true).MakeGenericType(dto);
        var send = _apiType.GetMethod("SendJsonAsync", Flags).MakeGenericMethod(dto);
        var prefix = typeof(SessionRecoveryRegression).GetMethod(nameof(TransportPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            .MakeGenericMethod(_responseType);
        var harmony = new Harmony("screenstocks.bridge.session-recovery-regression");
        try
        {
            harmony.Patch(send, prefix: new HarmonyMethod(prefix));
            VerifyBackoffCancellation();
            VerifyOutstandingRequestAndLateResponse();
            VerifyNeverCompletingRequest();
            using (var recoveryService = new TradeRecoveryService())
            {
                var recoveryHooks = new Harmony("screenstocks.bridge.production-recovery-regression");
                try
                {
                    NativeRecoveryAttempt.ValidateSurface();
                    OwnedApiRequests.Install(recoveryHooks);
                    TradeRecoveryHooks.Install(recoveryHooks);
                    VerifyOwnedRecovery();
                    VerifyBlockedAdmission(recoveryService);
                }
                finally { recoveryHooks.UnpatchSelf(); }
            }
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static bool TransportPrefix<T>(ref Task<T> __result)
    {
        Interlocked.Increment(ref _requests);
        __result = (Task<T>)_transport;
        return false;
    }

    private sealed class Fixture
    {
        internal object Api = null!;
        internal object Broker = null!;
        internal object Command = null!;
        internal CancellationToken Token;
        internal SemaphoreSlim TradeGate = null!;
        internal SemaphoreSlim ApiGate = null!;
        internal Task Recovery = null!;
        internal object ResponseSource = null!;
    }

    private static object Get(object instance, string field) => instance.GetType().GetField(field, Flags).GetValue(instance);
    private static void Set(object instance, string field, object? value) => instance.GetType().GetField(field, Flags).SetValue(instance, value);
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, Flags).Invoke(instance, args);
    private static bool Ambiguous(Fixture f) => (bool)_brokerType.GetProperty("HasAmbiguousTrade").GetValue(f.Broker);
    private static Task SourceTask(object source) => (Task)source.GetType().GetProperty("Task").GetValue(source);
    private static object NewSource() => Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(_responseType));

    private static Fixture NewFixture()
    {
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _notifications, 0);
        var session = FormatterServices.GetUninitializedObject(_game.GetType("ScreenStocks.Net.AuthSession", true));
        Set(session, "<AccessToken>k__BackingField", "synthetic-test-token-never-transmitted");
        Set(session, "<SteamId>k__BackingField", "synthetic-test-principal");
        var api = Activator.CreateInstance(_apiType, new[] { (object)"https://invalid.test", session, null! });
        var capability = Enum.ToObject(_game.GetType("ScreenStocks.Net.DurableCommandCapabilityState", true), 1);
        Set(api, "<DurableCommandCapability>k__BackingField", capability);
        var broker = Activator.CreateInstance(_brokerType, new object?[] { api, null, null, null, 0, null });
        var command = Activator.CreateInstance(_game.GetType("RemoteTradeBroker+PendingTradeCommand", true), true);
        var body = Activator.CreateInstance(_game.GetType("ScreenStocks.Net.TradeRequestDto", true));
        Set(body, "stockId", "$TEST");
        Set(body, "idempotencyKey", Guid.NewGuid().ToString("N"));
        Set(command, "Body", body);
        Set(command, "Path", "/trade/short");
        Set(command, "StockId", "$TEST");
        Set(command, "RequestId", Guid.NewGuid());
        Set(command, "PrincipalSteamId", "synthetic-test-principal");
        Set(command, "CapabilityAtFirstDispatch", capability);
        Set(command, "RecoveryStartedTimestamp", Stopwatch.GetTimestamp());
        Set(broker, "_ambiguousCommand", command);

        var notification = _brokerType.GetEvent("OnAmbiguousTradeResolved");
        var arguments = notification.EventHandlerType.GetGenericArguments().Select(t => Expression.Parameter(t)).ToArray();
        notification.AddEventHandler(broker, Expression.Lambda(notification.EventHandlerType,
            Expression.Call(typeof(SessionRecoveryRegression).GetMethod(nameof(Notify))), arguments).Compile());

        var fixture = new Fixture {
            Api = api, Broker = broker, Command = command,
            Token = ((CancellationTokenSource)Get(broker, "_sessionCancellation")).Token,
            TradeGate = (SemaphoreSlim)Get(broker, "_tradeDispatchGate"),
            ApiGate = (SemaphoreSlim)Get(api, "_durableCommandGate"), ResponseSource = NewSource()
        };
        _transport = SourceTask(fixture.ResponseSource);
        // Native SendTrade transfers this already-owned gate to recovery.
        Require(fixture.TradeGate.Wait(0), "seed recovery gate ownership");
        return fixture;
    }

    public static void Notify() => Interlocked.Increment(ref _notifications);

    private static void StartRecovery(Fixture f) => f.Recovery = (Task)Call(f.Broker,
        "RecoverAmbiguousCommandAsync", f.Command, f.Token, 0)!;

    private static void Reset(Fixture f)
    {
        // These are the actual API/broker operations called by the auth-manager
        // session boundary. Scene managers are not constructed in this harness.
        Call(f.Api, "InvalidateDurableCommandSession");
        Call(f.Broker, "ResetPendingCommandForSessionBoundary");
        Require(f.Token.IsCancellationRequested, "old recovery token cancelled");
        Require(!Ambiguous(f), "ambiguity flag cleared");
        Require(SourceTask(Get(f.Command, "Completion")).IsCompleted, "command waiter released");
    }

    private static void CompleteResponse(object source, bool accepted = false)
    {
        object response;
        if (accepted)
        {
            var dto = Activator.CreateInstance(_game.GetType("ScreenStocks.Net.TradeResultDto", true));
            Set(dto, "stockId", "$TEST");
            Set(dto, "accepted", true);
            response = _responseType.GetMethod("Ok", BindingFlags.Static | BindingFlags.Public).Invoke(null, new[] { dto });
        }
        else response = _responseType.GetMethod("Fail", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object?[] { 400, "SyntheticRejected", null, 0 });
        source.GetType().GetMethod("SetResult").Invoke(source, new[] { response });
    }

    private static void VerifyBackoffCancellation()
    {
        var f = NewFixture();
        StartRecovery(f);
        Require(Volatile.Read(ref _requests) == 0, "recovery is in cancellable retry delay");
        Reset(f);
        Require(f.Recovery.Wait(3000), "retry delay cancellation exits recovery");
        Require(f.TradeGate.CurrentCount == 1 && f.ApiGate.CurrentCount == 1, "backoff reset releases all owned gates");
        Console.WriteLine("PASS: reset during retry backoff completes waiters and releases trade gate");
    }

    private static void VerifyOutstandingRequestAndLateResponse()
    {
        var f = NewFixture();
        StartRecovery(f);
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref _requests) == 1, 3000), "recovery entered controlled HTTP await");
        Require(f.TradeGate.CurrentCount == 0 && f.ApiGate.CurrentCount == 0, "HTTP await owns both gates");
        var barrier = (Task)Call(f.Broker, "AcquireProgressResetBarrierAsync")!;
        Require(!barrier.IsCompleted, "IPO barrier waits behind recovery");
        var staleQueued = (Task)Call(f.Broker, "SendCapturedCommandAsync", f.Command)!;
        Require(!staleQueued.IsCompleted && Volatile.Read(ref _requests) == 1, "old queued request waits before reset");
        Reset(f);
        Require(!f.Recovery.IsCompleted && f.TradeGate.CurrentCount == 0 && f.ApiGate.CurrentCount == 0,
            "reset does not cancel HTTP await or release its gates");
        Require(!barrier.IsCompleted, "IPO barrier remains queued after session reset");

        var newSource = NewSource();
        _transport = SourceTask(newSource);
        var pending = (Task)Call(f.Broker, "SendCapturedCommandAsync", f.Command)!;
        Require(!pending.IsCompleted && Volatile.Read(ref _requests) == 1, "new durable request still waits on old API gate");
        CompleteResponse(f.ResponseSource, accepted: true);
        Require(f.Recovery.Wait(3000), "late HTTP response lets old recovery exit");
        Require(staleQueued.Wait(3000), "old queued request exits after gate drains");
        var staleResult = staleQueued.GetType().GetProperty("Result").GetValue(staleQueued);
        Require((string)Get(staleResult, "Error") == "CommandSessionChangedBeforeDispatch", "old queued command fenced before transport dispatch");
        Require(barrier.Wait(3000), "old queued IPO barrier eventually acquires trade gate");
        Require((bool)_brokerType.GetProperty("IsProgressResetBarrierActive").GetValue(f.Broker), "reset did not discard old IPO barrier");
        Require(f.TradeGate.CurrentCount == 0, "acquired IPO barrier still owns trade gate");
        Require(Volatile.Read(ref _notifications) == 0, "late old-session result not published as recovered trade");
        Require((Guid)Get(f.Broker, "_lastRecoveredRequestId") == Guid.Empty, "late accepted response not stored as recovered result");
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref _requests) == 2, 3000), "new durable request proceeds after old HTTP finishes");
        CompleteResponse(newSource);
        Require(pending.Wait(3000) && f.ApiGate.CurrentCount == 1, "API gate released by normal request exit");
        Call(f.Broker, "ReleaseProgressResetBarrier");
        Require(f.TradeGate.CurrentCount == 1, "IPO owner must explicitly release its acquired gate");
        Console.WriteLine("PASS: reset during HTTP leaves both gates held until response; late result is fenced; queued IPO barrier survives reset");
    }

    private static void VerifyNeverCompletingRequest()
    {
        var f = NewFixture();
        StartRecovery(f);
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref _requests) == 1, 3000), "recovery entered never-completing HTTP await");
        Reset(f);
        Require(!f.Recovery.Wait(100), "session cancellation cannot finish non-cancellable HTTP await");
        Require(f.TradeGate.CurrentCount == 0 && f.ApiGate.CurrentCount == 0, "cleared flag conceals retained gate ownership");
        Console.WriteLine("PASS: never-completing HTTP task remains blocked after session reset despite cleared ambiguity flag");
        // Release only our synthetic transport task so the test leaves no work.
        CompleteResponse(f.ResponseSource);
        Require(f.Recovery.Wait(3000), "synthetic transport cleanup");
    }

    private sealed class Routine : System.Collections.IEnumerator
    {
        public object Current => null!;
        public bool MoveNext() => true;
        public void Reset() => throw new NotSupportedException();
    }

    private static void VerifyOwnedRecovery()
    {
        var f = NewFixture();
        var aborted = 0;
        var request = OwnedApiRequests.Track(f.Api, new Routine(), SourceTask(f.ResponseSource), (code, error) =>
        {
            var response = _responseType.GetMethod("Fail", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object?[] { code, error, null, 0 });
            f.ResponseSource.GetType().GetMethod("TrySetResult").Invoke(f.ResponseSource, new[] { response });
        }, () => aborted++);
        var resultWait = (Task)Call(f.Broker, "WaitForAmbiguousTradeResultAsync", Get(f.Command, "RequestId"))!;
        StartRecovery(f);
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref _requests) == 1, 3000), "native recovery owns both gates");
        var oldBarrier = (Task)Call(f.Broker, "AcquireProgressResetBarrierAsync")!;
        TradeRecoveryHooks.MarkBoundary((Guid)Get(f.Command, "RequestId"));
        Reset(f);
        Require(resultWait.IsFaulted && resultWait.Exception!.GetBaseException() is TradeOutcomeUnknownException,
            "original trade is reported unknown, never falsely rejected");
        Require(OwnedApiRequests.Cancel(f.Api), "owned request abort succeeds");
        Require(f.Recovery.Wait(3000), "owned cancellation drains real broker recovery task");
        try { oldBarrier.Wait(3000); } catch (AggregateException) { }
        Require(oldBarrier.IsCanceled || oldBarrier.IsFaulted, "old queued IPO acquisition is cancelled after acquiring and releasing its gate");
        Require(f.TradeGate.CurrentCount == 1 && f.ApiGate.CurrentCount == 1, "both actual gates released by their owners");
        Require(!(bool)_brokerType.GetProperty("IsProgressResetBarrierActive").GetValue(f.Broker), "orphan IPO barrier flags cleared");
        Require(!TradeRecoveryHooks.HasBarrierWaiters(f.Broker), "barrier waiter tracking drained");
        Require(aborted == 1 && !request.MoveNext() && Volatile.Read(ref _notifications) == 0, "no late native resume or recovered-result application");
        var freshBarrier = (Task)Call(f.Broker, "AcquireProgressResetBarrierAsync")!;
        Require(freshBarrier.Wait(3000), "later barrier can acquire normally");
        Call(f.Broker, "ReleaseProgressResetBarrier");
        Require(f.TradeGate.CurrentCount == 1, "later owner releases exactly once");
        Console.WriteLine("PASS: production request ownership drains native HTTP gates, cancels orphan IPO ownership, and reports outcome_unknown");
    }

    private sealed class BlockedAttempt : ITradeRecoveryAttempt
    {
        public bool IsCurrent => true;
        public bool HasFreshState => false;
        public bool IsReady => false;
        public void Begin() { }
        public bool Drain() => false;
        public Task Reload() => Task.CompletedTask;
        public void Reconnect() { }
        public void Cancel() { }
    }
    private static void VerifyBlockedAdmission(TradeRecoveryService service)
    {
        var command = new object();
        service.Controller.Tick(0, command, () => new BlockedAttempt());
        service.Controller.Tick(30, command, () => new BlockedAttempt());
        var f = NewFixture();
        // A new barrier is rejected before setting requested/held flags.
        f.TradeGate.Release();
        var barrier = (Task)Call(f.Broker, "AcquireProgressResetBarrierAsync")!;
        Require(barrier.IsCanceled || barrier.IsFaulted, "new native barrier blocked during recovery");
        Require(f.TradeGate.CurrentCount == 1 && !(bool)_brokerType.GetProperty("IsProgressResetBarrierActive").GetValue(f.Broker),
            "blocked barrier did not acquire or alter native ownership");
        var source = new TaskCompletionSource<bool>(); var moves = 0;
        var mutation = OwnedApiRequests.Track(new object(), new Routine(), source.Task,
            (code, _) => { Require(code == 409, "new mutation is definitively not dispatched"); source.TrySetResult(false); }, () => moves++, mutation: true);
        Require(!mutation.MoveNext() && source.Task.IsCompleted, "native mutation cannot dispatch during recovery");
        Require(OwnedApiRequests.IsMutation("POST", "/trade/short") && !OwnedApiRequests.IsMutation("POST", "/auth/refresh") &&
            !OwnedApiRequests.IsMutation("GET", "/state"), "authentication and read refresh remain available");
        Console.WriteLine("PASS: recovery admission blocks native mutations and IPO barriers without taking ownership");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Session recovery assertion failed: " + label);
    }
}
