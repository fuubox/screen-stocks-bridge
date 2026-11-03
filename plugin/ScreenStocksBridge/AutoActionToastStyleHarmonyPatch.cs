using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace ScreenStocksBridge
{
    internal static class AutoActionToastStyleHarmonyPatch
    {
        internal static void Install(Harmony harmony)
        {
            var init = typeof(AutoActionToastController).GetMethod("Init",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(AutoAction) }, null);
            var postfix = typeof(AutoActionToastStyleHarmonyPatch).GetMethod(nameof(AfterInit),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (init == null || postfix == null)
                throw new MissingMethodException("Could not locate the auto-action toast initialization method.");

            harmony.Patch(init, postfix: new HarmonyMethod(postfix));
        }

        private static void AfterInit(AutoActionToastController __instance)
        {
            if (__instance == null) return;

            var root = __instance.transform;
            foreach (var image in root.GetComponentsInChildren<Image>(true))
                image.enabled = false;
            foreach (var outline in root.GetComponentsInChildren<Outline>(true))
                outline.enabled = false;

            var actionTextField = typeof(AutoActionToastController).GetField("actionText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var text = actionTextField?.GetValue(__instance) as TMP_Text;
            if (text == null) return;

            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.maxVisibleLines = 1;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.verticalAlignment = VerticalAlignmentOptions.Middle;

            var textRect = text.rectTransform;
            var rootRect = __instance.GetComponent<UnityEngine.RectTransform>();
            var oneLineHeight = text.GetPreferredValues("Ag", float.PositiveInfinity, float.PositiveInfinity).y;
            if (float.IsNaN(oneLineHeight) || float.IsInfinity(oneLineHeight) || oneLineHeight <= 0f)
                oneLineHeight = text.fontSize;

            if (textRect != null && oneLineHeight > 0f)
            {
                var oldTextHeight = textRect.rect.height;
                textRect.SetSizeWithCurrentAnchors(UnityEngine.RectTransform.Axis.Vertical, oneLineHeight);

                // Preserve the existing top and bottom padding while removing spare line height.
                if (rootRect != null && textRect.parent == rootRect && rootRect.rect.height >= oldTextHeight &&
                    oldTextHeight > 0f)
                {
                    var verticalPadding = rootRect.rect.height - oldTextHeight;
                    rootRect.SetSizeWithCurrentAnchors(UnityEngine.RectTransform.Axis.Vertical,
                        oneLineHeight + verticalPadding);
                }
            }
        }
    }
}
