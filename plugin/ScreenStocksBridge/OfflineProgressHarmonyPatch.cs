using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ScreenStocksBridge
{
    internal static class OfflineProgressHarmonyPatch
    {
        internal static OfflineProgressCaptureService? CaptureService;
        internal static bool AutoClose;

        internal static void Install(Harmony harmony)
        {
            var original = typeof(OfflineProgressView).GetMethod(nameof(OfflineProgressView.Show));
            var postfix = typeof(OfflineProgressHarmonyPatch).GetMethod(nameof(AfterShow),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (original == null || postfix == null)
                throw new MissingMethodException("Could not locate the offline progress view methods.");

            harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        }

        private static void AfterShow(OfflineProgressView __instance, BigNumber total, BigNumber generators,
            BigNumber dividends, BigNumber autoActions, float secondsAway, float cappedEarningsSeconds,
            List<PositionChangeResult> positionResults, bool showEarnings)
        {
            CaptureService?.Capture(total, generators, dividends, autoActions, secondsAway,
                cappedEarningsSeconds, positionResults, showEarnings);
            if (AutoClose) __instance.Hide();
        }
    }
}
