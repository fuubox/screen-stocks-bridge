using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace ScreenStocksBridge
{
    // Protect the queue handoff without rewriting the game's async MoveNext/EH.
    internal static class ManualTradeQueueHarmonyPatch
    {
        private static FieldInfo _tail = null!;
        private static Type _previewType = null!;

        internal static void Install(Harmony harmony)
        {
            if (typeof(StockManager).Module.ModuleVersionId != new Guid("da6422d0-3325-4c79-bfe4-ef9f5955c92b"))
                throw new InvalidOperationException("Manual trade queue patch requires a reviewed game build.");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var method = typeof(StockManager).GetMethod("RunQueuedManualTradeAsync", flags)
                ?? throw new MissingMethodException("StockManager.RunQueuedManualTradeAsync was not found.");
            _tail = typeof(StockManager).GetField("_manualActionTail", flags)
                ?? throw new MissingFieldException("StockManager", "_manualActionTail");
            var parameters = method.GetParameters();
            if (_tail.FieldType != typeof(Task) || method.ReturnType != typeof(Task<bool>) ||
                parameters.Length != 6 || parameters[2].Name != "preview")
                throw new InvalidOperationException("Manual trade queue signature has changed.");
            _previewType = parameters[2].ParameterType;
            if (!_previewType.IsGenericType || _previewType.GetGenericTypeDefinition() != typeof(Action<>))
                throw new InvalidOperationException("Manual trade preview signature has changed.");
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(ManualTradeQueueHarmonyPatch).GetMethod(nameof(Prefix),
                    BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(_previewType.GetGenericArguments())),
                postfix: new HarmonyMethod(typeof(ManualTradeQueueHarmonyPatch), nameof(Postfix)));
        }

        private static void Prefix<TAction>(object __instance, ref Action<TAction>? preview, out QueueCall __state)
        {
            var call = __state = new QueueCall(__instance);
            var original = preview;
            preview = action => { call.CaptureTail(); original?.Invoke(action); };
        }

        private static void Postfix(Task<bool> __result, QueueCall __state) => __state.ObserveExit(__result);

        private sealed class QueueCall
        {
            private readonly object _manager;
            private readonly TaskCompletionSource<bool> _exited = new TaskCompletionSource<bool>();
            private bool _captured;

            internal QueueCall(object manager) { _manager = manager; }

            // Preview is invoked after this action publishes its native tail, but
            // before callbacks can re-enter trading or execution can suspend.
            public void CaptureTail()
            {
                if (_captured) return;
                var nativeTail = (Task)_tail.GetValue(_manager);
                _tail.SetValue(_manager, Task.WhenAny(nativeTail, _exited.Task));
                _captured = true;
            }

            internal void ObserveExit(Task<bool> action)
            {
                // Completion only: no game object access on this continuation.
                // Retain the native result/exception on the original action Task.
                action.ContinueWith(_ => _exited.TrySetResult(true), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
