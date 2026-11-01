using System;
using System.Reflection;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class HumanActivityFocusIndicator : IDisposable
    {
        private static readonly FieldInfo? GraphField = typeof(StockGraphVisualizer).GetField(
            "_lineGraph", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ActivityHeightField = typeof(UILineGraph).GetField(
            "_humanActivityMaxBarHeightFraction", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Vector3[] _corners = new Vector3[4];
        private UILineGraph? _graph;
        private StockGraphVisualizer? _visualizer;
        private GUIStyle? _style;
        private GUIStyle? _watermarkStyle;
        private float _nextSearchAt;
        private string _lastReportedFocus = string.Empty;

        internal void Draw(string activeStockId, bool showTrackingDebugText, bool showOverrideText,
            bool showPatchWatermark, Action<string> log)
        {
            var hasFocus = !string.IsNullOrEmpty(activeStockId);
            if (!showPatchWatermark && (!hasFocus || (!showTrackingDebugText && !showOverrideText)))
            {
                _lastReportedFocus = string.Empty;
                return;
            }
            if (!hasFocus) _lastReportedFocus = string.Empty;
            if ((_graph == null || !_graph.isActiveAndEnabled || !_graph.gameObject.activeInHierarchy) &&
                Time.unscaledTime >= _nextSearchAt)
            {
                _nextSearchAt = Time.unscaledTime + 1f;
                _graph = null;
                foreach (var visualizer in UnityEngine.Object.FindObjectsByType<StockGraphVisualizer>(FindObjectsSortMode.None))
                {
                    var graph = GraphField?.GetValue(visualizer) as UILineGraph;
                    if (graph == null || !graph.isActiveAndEnabled || !graph.gameObject.activeInHierarchy) continue;
                    _graph = graph;
                    _visualizer = visualizer;
                    break;
                }
                log(_graph == null ? "Activity indicator: waiting for active stock graph." :
                    "Activity indicator: found graph " + _graph.name + ", local rect " + _graph.GraphRectTransform.rect);
            }
            if (_graph == null || !_graph.isActiveAndEnabled || !_graph.gameObject.activeInHierarchy) return;
            foreach (var group in _graph.GetComponentsInParent<CanvasGroup>())
                if (group.alpha <= 0.01f) return;

            // Project the actual graph into screen pixels. Local graph units need not be pixels.
            var canvas = _graph.canvas;
            if (canvas == null || !canvas.isActiveAndEnabled) return;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            _graph.GraphRectTransform.GetWorldCorners(_corners);
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (var corner in _corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            var width = max.x - min.x;
            var height = max.y - min.y;
            var fraction = ActivityHeightField?.GetValue(_graph) is float value ? value : 0.18f;
            var localRect = _graph.GraphRectTransform.rect;
            var reserved = _graph.PriceAreaMinY - localRect.yMin;
            if (reserved > 0f && localRect.height > 0f)
                fraction = Mathf.Min(fraction, reserved / localRect.height);
            var bandHeight = height * Mathf.Clamp01(fraction);
            if (width <= 2f || bandHeight <= 2f) return;
            var band = new Rect(min.x, Screen.height - min.y - bandHeight, width, bandHeight);
            var tracking = hasFocus && _visualizer != null &&
                string.Equals(activeStockId, _visualizer.CurrentStockId, StringComparison.Ordinal);
            var statusText = hasFocus && (tracking ? showTrackingDebugText : showOverrideText)
                ? tracking ? "TRACKING" : "OVERRIDE: ON"
                : null;
            if (!showPatchWatermark && statusText == null) return;
            if (hasFocus && _lastReportedFocus != activeStockId)
            {
                log("Activity indicator: focus=" + activeStockId + ", displayed=" + _visualizer?.CurrentStockId +
                    ", histogram screen rect=" + band);
                _lastReportedFocus = activeStockId;
            }

            var scale = width / 480f;
            var padding = Mathf.Min(4f * scale, bandHeight * 0.1f);
            var rect = new Rect(padding, padding, width - 2f * padding, bandHeight - 2f * padding);
            var trackingFontSize = Mathf.Max(6, Mathf.RoundToInt(8f * scale));
            var watermarkStyle = _watermarkStyle ?? (_watermarkStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerLeft,
                wordWrap = false,
                padding = new RectOffset(),
            });
            var style = _style ?? (_style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerLeft,
                wordWrap = false,
                padding = new RectOffset(),
            });
            watermarkStyle.fontStyle = FontStyle.Normal;
            watermarkStyle.fontSize = trackingFontSize;
            style.fontStyle = tracking ? FontStyle.Normal : FontStyle.Bold;
            style.fontSize = statusText == null || tracking
                ? trackingFontSize
                : Mathf.Max(6, Mathf.RoundToInt(14f * scale));
            var watermarkContent = new GUIContent("BRIDGE-MOD");
            var statusContent = statusText == null ? null : new GUIContent(statusText);
            var gap = Mathf.Min(2f * scale, rect.height * 0.05f);
            while ((showPatchWatermark && watermarkStyle.fontSize > 4) ||
                   (statusContent != null && style.fontSize > 4))
            {
                var watermarkSize = showPatchWatermark ? watermarkStyle.CalcSize(watermarkContent) : Vector2.zero;
                var statusSize = statusContent == null ? Vector2.zero : style.CalcSize(statusContent);
                var combinedHeight = (showPatchWatermark ? watermarkSize.y : 0f) +
                    (statusContent == null ? 0f : statusSize.y +
                        (showPatchWatermark && statusContent != null ? gap : 0f));
                if ((!showPatchWatermark || watermarkSize.x <= rect.width) &&
                    (statusContent == null || statusSize.x <= rect.width) && combinedHeight <= rect.height)
                    break;
                if (showPatchWatermark && watermarkStyle.fontSize > 4) watermarkStyle.fontSize--;
                if (statusContent != null && style.fontSize > 4) style.fontSize--;
            }

            // Clip drawing to the histogram's screen bounds, independent of the game's TMP masks.
            var previousColor = GUI.color;
            GUI.color = Color.white;
            GUI.BeginGroup(band);
            try
            {
                var watermarkColor = new Color(0.72f, 0.8f, 0.88f, 0.75f);
                var statusColor = tracking ? new Color(0.35f, 0.9f, 0.5f, 1f) : new Color(1f, 0.78f, 0.3f, 1f);
                if (showPatchWatermark && statusContent != null)
                {
                    var watermarkHeight = watermarkStyle.CalcSize(watermarkContent).y;
                    var statusHeight = style.CalcSize(statusContent).y;
                    var top = rect.y + Mathf.Max(0f, rect.height - watermarkHeight - statusHeight - gap);
                    DrawLabel(new Rect(rect.x, top, rect.width, watermarkHeight), watermarkContent,
                        watermarkStyle, watermarkColor);
                    DrawLabel(new Rect(rect.x, top + watermarkHeight + gap, rect.width, statusHeight),
                        statusContent, style, statusColor);
                }
                else if (showPatchWatermark)
                {
                    DrawLabel(rect, watermarkContent, watermarkStyle, watermarkColor);
                }
                else if (statusContent != null)
                {
                    DrawLabel(rect, statusContent, style, statusColor);
                }
            }
            finally
            {
                GUI.EndGroup();
                GUI.color = previousColor;
            }
        }

        private static void DrawLabel(Rect rect, GUIContent content, GUIStyle style, Color color)
        {
            style.normal.textColor = Color.black;
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), content, style);
            style.normal.textColor = color;
            GUI.Label(rect, content, style);
        }

        public void Dispose()
        {
            _graph = null;
            _visualizer = null;
            _style = null;
            _watermarkStyle = null;
        }
    }
}
