using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using HarmonyLib;

// Invoked in an isolated process using the game's own Mono and Harmony builds.
// No Unity scene, credentials, HTTP dispatch, save data, or live game is used.
public static class QueueRegression
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type _stock = null!;
    private static Assembly _game = null!;

    public static int Run()
    {
        try
        {
            _game = Assembly.Load("Assembly-CSharp");
            _stock = _game.GetType("StockManager", true);
            BrokerCloseRegression.Run();
            VerifyCleanup(false);
            var harmony = new Harmony("screenstocks.bridge.queue-regression");
            try
            {
                var plugin = Assembly.Load("ScreenStocksBridge");
                var patch = plugin.GetType("ScreenStocksBridge.ManualTradeQueueHarmonyPatch", true);
                patch.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { harmony });
                VerifyCleanup(true);
                VerifyExecutionFault();
                VerifyReentrantPreview();
                VerifyPreviewFault();
                VerifyRecoveryGuard(plugin);
            }
            finally { harmony.UnpatchSelf(); }
            SessionRecoveryRegression.Run(_game);
            OwnedRequestRegression.Run(_game);
            Console.WriteLine("PASS: installed Mono/Harmony patch regression checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static object NewManager()
    {
        var manager = FormatterServices.GetUninitializedObject(_stock);
        Set(manager, "_usingRemote", true);
        Set(manager, "_heldManualStocks", new HashSet<string>());
        Set(manager, "_pending", Activator.CreateInstance(_game.GetType("PendingTradeRegistry", true))!);
        Set(manager, "_manualActionTail", Task.CompletedTask);
        return manager;
    }

    private static void Set(object instance, string field, object value) =>
        instance.GetType().GetField(field, Fields).SetValue(instance, value);

    private static Task<bool> Start(object manager, string id, Task<bool> execution, Action? preview = null)
    {
        var action = _game.GetType("StockManager+ManualTradeAction", true);
        var parameter = Expression.Parameter(action, "action");
        var execute = Expression.Lambda(typeof(Func<,>).MakeGenericType(action, typeof(Task<bool>)),
            Expression.Constant(execution, typeof(Task<bool>)), parameter).Compile();
        var zero = Activator.CreateInstance(_game.GetType("BigNumber", true));
        var previewDelegate = preview == null ? null : Expression.Lambda(typeof(Action<>).MakeGenericType(action),
            Expression.Invoke(Expression.Constant(preview)), parameter).Compile();
        return (Task<bool>)_stock.GetMethod("RunQueuedManualTradeAsync", Fields).Invoke(manager,
            new object?[] { id, execute, previewDelegate, null, zero, zero });
    }

    private static void State(object manager, int queued, bool allowed)
    {
        var actual = (int)_stock.GetField("_queuedManualActions", Fields).GetValue(manager);
        Require(actual == queued, "queue count expected=" + queued + " actual=" + actual);
        Require((bool)_stock.GetProperty("CanStartManualTrade").GetValue(manager) == allowed, "admission state");
    }

    private static void VerifyCleanup(bool patched)
    {
        var manager = NewManager();
        var source = new TaskCompletionSource<bool>();
        var first = Start(manager, "first", source.Task);
        var second = Start(manager, "second", source.Task);
        var third = Start(manager, "third", source.Task);
        State(manager, 3, false);
        Require(!first.IsCompleted && !second.IsCompleted && !third.IsCompleted, "no premature tail completion");
        var rejected = Start(manager, "full", source.Task);
        Require(rejected.Status == TaskStatus.RanToCompletion && !rejected.Result, "full queue rejects");
        State(manager, 3, false);

        var registry = _stock.GetField("_pending", Fields).GetValue(manager);
        Set(registry, "OnPendingChanged", new Action<string>(_ => throw new InvalidOperationException("Injected cleanup failure")));
        source.SetResult(false);
        Set(registry, "OnPendingChanged", null!);
        Require(first.IsFaulted && first.Exception!.GetBaseException().Message == "Injected cleanup failure", "original exception survives");
        if (!patched)
        {
            Require(!second.IsCompleted && !third.IsCompleted, "unpatched waiters stranded");
            State(manager, 2, true);
            Start(manager, "fourth", source.Task);
            State(manager, 3, false);
            Console.WriteLine("PASS: unpatched cleanup reproduces permanent queue lock");
            return;
        }

        try { Task.WhenAll(first, second, third).Wait(3000); } catch (AggregateException) { }
        State(manager, 0, true);
        Require(second.IsFaulted && third.IsFaulted, "all faulted actions exit without stranding tails");
        var next = Start(manager, "next", Task.FromResult(true));
        Require(next.Status == TaskStatus.RanToCompletion && next.Result, "next healthy action succeeds");
        State(manager, 0, true);
        Console.WriteLine("PASS: patch preserves active waits, releases faulted tails, and permits subsequent trades");
    }

    private static void VerifyExecutionFault()
    {
        var manager = NewManager();
        var source = new TaskCompletionSource<bool>();
        var first = Start(manager, "first", source.Task);
        var second = Start(manager, "second", Task.FromResult(true));
        State(manager, 2, true);
        Require(!second.IsCompleted, "second action waits for first");
        source.SetException(new InvalidOperationException("Injected dispatch failure"));
        Require(first.IsFaulted && first.Exception!.GetBaseException().Message == "Injected dispatch failure", "dispatch fault preserved");
        Require(second.Status == TaskStatus.RanToCompletion && second.Result, "dispatch fault allows later action");
        State(manager, 0, true);
        Console.WriteLine("PASS: dispatch failure preserves exception and queue ordering");
    }

    private static void VerifyReentrantPreview()
    {
        var manager = NewManager();
        var source = new TaskCompletionSource<bool>();
        Task<bool>? nested = null;
        var first = Start(manager, "first", source.Task, () => nested = Start(manager, "nested", Task.FromResult(true)));
        Require(nested != null && !nested.IsCompleted, "reentrant preview cannot overtake parent");
        var registry = _stock.GetField("_pending", Fields).GetValue(manager);
        Set(registry, "OnPendingChanged", new Action<string>(_ => throw new InvalidOperationException("Nested cleanup failure")));
        source.SetResult(false);
        try { Task.WhenAll(first, nested!).Wait(3000); } catch (AggregateException) { }
        Require(first.IsFaulted && nested!.IsFaulted, "reentrant actions both exit with original faults");
        State(manager, 0, true);
        Console.WriteLine("PASS: reentrant preview preserves ordering and survives parent cleanup failure");
    }

    private static void VerifyPreviewFault()
    {
        var manager = NewManager();
        var failed = Start(manager, "first", Task.FromResult(true), () => throw new InvalidOperationException("Preview failure"));
        Require(failed.IsFaulted && failed.Exception!.GetBaseException().Message == "Preview failure", "preview fault preserved");
        var healthy = Start(manager, "second", Task.FromResult(true));
        Require(healthy.Status == TaskStatus.RanToCompletion && healthy.Result, "preview fault permits later trade");
        State(manager, 0, true);
        Console.WriteLine("PASS: preview exception does not strand queue");
    }

    private static void VerifyRecoveryGuard(Assembly plugin)
    {
        var guard = plugin.GetType("ScreenStocksBridge.TradeRecoveryState", true)
            .GetMethod("IsPending", BindingFlags.Static | BindingFlags.NonPublic);
        var manager = NewManager();
        Require(!(bool)guard.Invoke(null, new[] { manager }), "healthy manager allows submissions");
        var brokerType = _game.GetType("RemoteTradeBroker", true);
        var broker = FormatterServices.GetUninitializedObject(brokerType);
        Set(broker, "_sessionLock", new object());
        Set(manager, "_remoteTradeBroker", broker);
        Require(!(bool)guard.Invoke(null, new[] { manager }), "idle broker allows submissions");
        var command = Activator.CreateInstance(_game.GetType("RemoteTradeBroker+PendingTradeCommand", true), true);
        Set(broker, "_ambiguousCommand", command!);
        Require((bool)guard.Invoke(null, new[] { manager }), "uncertain command blocks trade/IPO dispatch");
        Set(broker, "_ambiguousCommand", null!);
        Require(!(bool)guard.Invoke(null, new[] { manager }), "settled broker permits dispatch again");
        Console.WriteLine("PASS: bridge recovery guard follows actual broker ambiguity state");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
    }
}
