using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ScreenStocks.Net;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal static class AutoActionUnlockHarmonyPatch
    {
        private static readonly AsyncSuccessSignal LevelFetchRefresh = new AsyncSuccessSignal();
        private static int _unlockUiRefreshPending;

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

        internal static void InstallLevelDataRefresh(Harmony harmony)
        {
            var target = typeof(RemoteLevelsClient).GetMethod(nameof(RemoteLevelsClient.FetchAsync),
                BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            if (target == null)
                throw new MissingMethodException("RemoteLevelsClient.FetchAsync(bool) was not found.");

            var postfix = new HarmonyMethod(typeof(AutoActionUnlockHarmonyPatch).GetMethod(
                nameof(LevelFetchPostfix), BindingFlags.Static | BindingFlags.NonPublic));
            harmony.Patch(target, postfix: postfix);
        }

        internal static bool TryConsumeLevelDataRefresh() => LevelFetchRefresh.TryConsume();

        internal static void SyncClaimedRewardsFromRemoteLevels()
        {
            var game = GameManager.I;
            if (game == null) return;

            var sync = typeof(GameManager).GetMethod("SyncClaimedLevelsFromServer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (sync == null)
                throw new MissingMethodException("GameManager.SyncClaimedLevelsFromServer() was not found.");
            sync.Invoke(game, null);
            Interlocked.Exchange(ref _unlockUiRefreshPending, 1);
        }

        internal static bool HasPendingUnlockUiRefresh => Volatile.Read(ref _unlockUiRefreshPending) != 0;

        internal static int RefreshUnlockUiInstances()
        {
            var check = typeof(AutoActionUnlockUI).GetMethod("Check",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (check == null)
                throw new MissingMethodException("AutoActionUnlockUI.Check() was not found.");

            var refreshed = 0;
            var instances = Resources.FindObjectsOfTypeAll<AutoActionUnlockUI>();
            foreach (var instance in instances)
            {
                if (instance == null || !instance.gameObject.scene.IsValid()) continue;
                check.Invoke(instance, null);
                refreshed++;
            }

            if (refreshed > 0) Interlocked.Exchange(ref _unlockUiRefreshPending, 0);
            return refreshed;
        }

        private static void LevelFetchPostfix(Task<bool> __result)
        {
            if (__result != null) LevelFetchRefresh.Observe(__result);
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
