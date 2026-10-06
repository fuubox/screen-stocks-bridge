using System;
using System.Reflection;
using HarmonyLib;

namespace ScreenStocksBridge
{
    internal static class AutoActionToastHarmonyPatch
    {
        internal static AutoActionToastService? CaptureService;

        internal static void Install(Harmony harmony)
        {
            var init = typeof(AutoActionToastController).GetMethod("Init",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(AutoAction) }, null);
            var postfix = typeof(AutoActionToastHarmonyPatch).GetMethod(nameof(AfterInit),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (init == null || postfix == null)
                throw new MissingMethodException("Could not locate the auto-action toast initialization method.");

            harmony.Patch(init, postfix: new HarmonyMethod(postfix));
        }

        private static void AfterInit(AutoActionToastController __instance, AutoAction __0)
        {
            if (__instance == null || __0 == null) return;

            var actionTextField = typeof(AutoActionToastController).GetField("actionText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var textComponent = actionTextField?.GetValue(__instance);
            var textProperty = textComponent?.GetType().GetProperty("text",
                BindingFlags.Instance | BindingFlags.Public);
            var displayedText = textProperty?.GetValue(textComponent, null) as string ?? string.Empty;
            CaptureService?.Capture(__0, displayedText);
        }
    }
}
