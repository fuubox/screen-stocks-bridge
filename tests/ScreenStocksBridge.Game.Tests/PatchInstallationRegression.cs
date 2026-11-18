using System;
using System.Reflection;
using HarmonyLib;
using ScreenStocksBridge;
internal static class PatchInstallationRegression
{
    internal static void Run()
    {
        var names = new[] { "AutoActionGraphMarkerHarmonyPatch", "PurchaseMarkerUnstackHarmonyPatch", "AutoActionUnlockHarmonyPatch", "AutoActionToastHarmonyPatch", "AutoActionToastStyleHarmonyPatch", "NewsTickerHarmonyPatch", "OfflineProgressHarmonyPatch", "NetWorthHistoryHarmonyPatch", "TransactionHistoryHarmonyPatch" };
        foreach (var name in names)
        {
            var harmony = new Harmony("screenstocks.patch.review." + name);
            try
            {
                var type = typeof(Plugin).Assembly.GetType("ScreenStocksBridge." + name, true);
                type.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { harmony });
                if (name == "AutoActionUnlockHarmonyPatch") type.GetMethod("InstallLevelDataRefresh", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { harmony });
                Console.WriteLine("PASS: patch installation " + name);
            }
            finally { harmony.UnpatchSelf(); }
        }
    }
}
