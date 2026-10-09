using System;
using System.Reflection;
using HarmonyLib;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal static class AutoActionUnlockHarmonyPatch
    {
        internal static void Install(Harmony harmony)
        {
            var target = typeof(GameManager).GetMethod(nameof(GameManager.IsAutoActionsUnlocked),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (target == null)
                throw new MissingMethodException("GameManager.IsAutoActionsUnlocked() was not found.");

            var postfix = new HarmonyMethod(typeof(AutoActionUnlockHarmonyPatch).GetMethod(
                nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(target, postfix: postfix);
        }

        private static void Postfix(GameManager __instance, ref bool __result)
        {
            if (__result || __instance == null || __instance.Settings == null ||
                !__instance.Settings.useRemoteServer) return;

            var levels = ScreenStocksAuthManager.I?.Levels;
            if (levels == null || !levels.HasLevelData) return;

            var catalog = levels.Levels;
            if (catalog == null) return;

            for (var index = 0; index < catalog.Count; index++)
            {
                var entry = catalog[index];
                if (entry == null || !entry.unlocksAutoActions || !levels.IsClaimed(entry.levelIndex)) continue;

                __result = true;
                return;
            }
        }
    }
}
