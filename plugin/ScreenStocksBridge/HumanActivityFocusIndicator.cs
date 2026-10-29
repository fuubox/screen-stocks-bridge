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
        private float _nextSearchAt;
        private string _lastReportedFocus = string.Empty;

        internal void Draw(string activeStockId, Action<string> log)
        {
            if (string.IsNullOrEmpty(activeStockId))
            {
                _lastReportedFocus = string.Empty;
                return;
            }
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
            var tracking = _visualizer != null &&
                string.Equals(activeStockId, _visualizer.CurrentStockId, StringComparison.Ordinal);
            var text = tracking ? "TRACKING" : "OVERRIDE: ON";
            if (_lastReportedFocus != activeStockId)
            {
                log("Activity indicator: focus=" + activeStockId + ", displayed=" + _visualizer?.CurrentStockId +
                    ", histogram screen rect=" + band);
                _lastReportedFocus = activeStockId;
            }

            var scale = width / 480f;
            var padding = Mathf.Min(4f * scale, bandHeight * 0.1f);
            var style = _style ?? (_style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.LowerLeft,
                wordWrap = false,
                padding = new RectOffset(),
            });
            style.fontStyle = tracking ? FontStyle.Normal : FontStyle.Bold;
            style.fontSize = Mathf.Max(6, Mathf.RoundToInt((tracking ? 8f : 14f) * scale));
            var content = new GUIContent(text);
            while (style.fontSize > 4 && (style.CalcSize(content).x > width - 2f * padding ||
                style.CalcSize(content).y > bandHeight - 2f * padding)) style.fontSize--;

            // Clip drawing to the histogram's screen bounds, independent of the game's TMP masks.
            var previousColor = GUI.color;
            GUI.color = Color.white;
            GUI.BeginGroup(band);
            try
            {
                var rect = new Rect(padding, padding, width - 2f * padding, bandHeight - 2f * padding);
                style.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), content, style);
                style.normal.textColor = tracking ? new Color(0.35f, 0.9f, 0.5f, 1f) : new Color(1f, 0.78f, 0.3f, 1f);
                GUI.Label(rect, content, style);
            }
            finally
            {
                GUI.EndGroup();
                GUI.color = previousColor;
            }
        }

        public void Dispose()
        {
            _graph = null;
            _visualizer = null;
            _style = null;
        }
    }
}
