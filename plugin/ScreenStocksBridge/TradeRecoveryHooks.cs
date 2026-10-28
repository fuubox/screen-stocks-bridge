using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal sealed class TradeOutcomeUnknownException : Exception
    {
        internal TradeOutcomeUnknownException() : base("The original trade outcome is unknown. Session recovery was started. Do not automatically resubmit it.") { }
    }

    internal static class TradeRecoveryHooks
    {
        private static long _epoch;
        private static readonly object Gate = new object();
        private static readonly HashSet<Guid> Unknown = new HashSet<Guid>();
        private static readonly Queue<Guid> UnknownOrder = new Queue<Guid>();
        private static readonly Dictionary<object, int> BarrierWaiters = new Dictionary<object, int>();
        internal static long Epoch => Interlocked.Read(ref _epoch);

        internal static void MarkBoundary(Guid requestId)
        {
            Interlocked.Increment(ref _epoch);
            lock (Gate)
            {
                if (Unknown.Add(requestId)) UnknownOrder.Enqueue(requestId);
                while (UnknownOrder.Count > 64) Unknown.Remove(UnknownOrder.Dequeue());
            }
        }

        internal static void Install(Harmony harmony)
        {
            ReviewedGameBuild.Require();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in new[] { typeof(RemoteTradeBroker), typeof(RemoteUpgradesClient), typeof(RemoteLevelsClient) })
            {
                var method = type.GetMethod("AcquireProgressResetBarrierAsync", flags)
                    ?? throw new MissingMethodException(type.FullName, "AcquireProgressResetBarrierAsync");
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(TradeRecoveryHooks), nameof(BarrierPrefix)),
                    postfix: new HarmonyMethod(typeof(TradeRecoveryHooks), nameof(BarrierPostfix)));
            }
            harmony.Patch(typeof(RemoteTradeBroker).GetMethod("WaitForAmbiguousTradeResultAsync", flags),
                postfix: new HarmonyMethod(typeof(TradeRecoveryHooks), nameof(WaitPostfix)));
            harmony.Patch(typeof(StockManager).GetMethod("StartManualTrade", flags),
                prefix: new HarmonyMethod(typeof(TradeRecoveryHooks), nameof(ManualPrefix)));
            harmony.Patch(typeof(StockManager).GetProperty("CanStartManualTrade", flags)!.GetGetMethod(true),
                postfix: new HarmonyMethod(typeof(TradeRecoveryHooks), nameof(AdmissionPostfix)));
            harmony.Patch(typeof(GameManager).GetMethod("TriggerIPO", flags),
                prefix: new HarmonyMethod(typeof(TradeRecoveryHooks), nameof(IpoPrefix)));
        }

        private static bool ManualPrefix(StockManager __instance, ref Task<bool> __result)
        {
            if (!TradeRecoveryService.BlocksCommands && !TradeRecoveryState.IsPending(__instance)) return true;
            __result = Task.FromResult(false);
            return false;
        }
        private static void AdmissionPostfix(StockManager __instance, ref bool __result)
        {
            if (TradeRecoveryService.BlocksCommands || TradeRecoveryState.IsPending(__instance)) __result = false;
        }
        private static bool IpoPrefix() => !TradeRecoveryService.BlocksCommands && !TradeRecoveryState.IsPending(StockManager.I);

        private static bool BarrierPrefix(ref Task __result, out long __state)
        {
            __state = Epoch;
            if (!TradeRecoveryService.BlocksCommands) return true;
            __result = Task.FromException(new OperationCanceledException("Trade session recovery is in progress."));
            return false;
        }
        private static void BarrierPostfix(object __instance, ref Task __result, long __state) =>
            __result = ObserveBarrier(__instance, __result, __state);

        internal static bool HasBarrierWaiters(object owner) { lock (Gate) return BarrierWaiters.TryGetValue(owner, out var count) && count > 0; }
        private static async Task ObserveBarrier(object owner, Task native, long epoch)
        {
            lock (Gate) { BarrierWaiters.TryGetValue(owner, out var count); BarrierWaiters[owner] = count + 1; }
            try
            {
                await native;
                if (epoch == Epoch) return;
                // Acquisition succeeded, but its old caller has not been given
                // ownership yet. Release exactly this acquisition, then cancel
                // the caller so it cannot later claim or double-release it.
                owner.GetType().GetMethod("ReleaseProgressResetBarrier",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(owner, null);
                throw new OperationCanceledException("The IPO barrier belonged to a recovered trade session.");
            }
            finally
            {
                lock (Gate)
                {
                    if (--BarrierWaiters[owner] == 0) BarrierWaiters.Remove(owner);
                }
            }
        }

        private static void WaitPostfix(Guid __0, ref Task<TradeResult> __result) => __result = ClassifyResult(__0, __result);
        private static async Task<TradeResult> ClassifyResult(Guid id, Task<TradeResult> native)
        {
            var result = await native;
            bool unknown;
            lock (Gate) unknown = Unknown.Contains(id);
            if (unknown && result.requestId == Guid.Empty) throw new TradeOutcomeUnknownException();
            return result;
        }
    }
}
