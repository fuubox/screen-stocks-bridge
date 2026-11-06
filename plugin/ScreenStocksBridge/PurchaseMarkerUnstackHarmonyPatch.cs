using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ScreenStocksBridge
{
    internal static class PurchaseMarkerUnstackHarmonyPatch
    {
        private const long ClusterWindowTicks = 15;
        private static readonly MethodInfo? DrawPurchaseMarkersMethod = typeof(UILineGraph).GetMethod(
            "DrawPurchaseMarkers", BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(VertexHelper), typeof(Rect) }, null);
        private static readonly FieldInfo? PurchaseMarkersField = typeof(UILineGraph).GetField(
            "_purchaseMarkers", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? VisibleDataCountField = typeof(UILineGraph).GetField(
            "_visibleDataCount", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? VisiblePointCountField = typeof(UILineGraph).GetField(
            "_visiblePointCount", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? FirstVisibleTickField = typeof(UILineGraph).GetField(
            "_firstVisibleTick", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? MinimumPriceField = typeof(UILineGraph).GetField(
            "_minValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? MaximumPriceField = typeof(UILineGraph).GetField(
            "_maxValue", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? MarkerRadiusField = typeof(UILineGraph).GetField(
            "_purchaseMarkerRadius", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? MarkerStrokeWidthField = typeof(UILineGraph).GetField(
            "_purchaseMarkerStrokeWidth", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type? PurchaseMarkerType = PurchaseMarkersField?.FieldType.GetGenericArguments()[0];
        private static readonly FieldInfo? MarkerDataIndexField = PurchaseMarkerType?.GetField(
            "DataIndex", BindingFlags.Instance | BindingFlags.Public);
        private static readonly FieldInfo? MarkerPriceField = PurchaseMarkerType?.GetField(
            "Price", BindingFlags.Instance | BindingFlags.Public);
        private static readonly FieldInfo? MarkerIsBuyField = PurchaseMarkerType?.GetField(
            "IsBuy", BindingFlags.Instance | BindingFlags.Public);

        private static bool _reportedFailure;

        internal static bool Enabled { get; set; }

        internal static void Install(Harmony harmony)
        {
            var prefix = typeof(PurchaseMarkerUnstackHarmonyPatch).GetMethod(
                nameof(BeforeDrawPurchaseMarkers), BindingFlags.Static | BindingFlags.NonPublic);
            var finalizer = typeof(PurchaseMarkerUnstackHarmonyPatch).GetMethod(
                nameof(RestoreAfterDraw), BindingFlags.Static | BindingFlags.NonPublic);

            if (DrawPurchaseMarkersMethod == null || PurchaseMarkersField == null ||
                VisibleDataCountField == null || VisiblePointCountField == null || FirstVisibleTickField == null ||
                MinimumPriceField == null || MaximumPriceField == null ||
                MarkerRadiusField == null || MarkerStrokeWidthField == null ||
                MarkerDataIndexField == null || MarkerPriceField == null ||
                MarkerIsBuyField == null || prefix == null || finalizer == null)
                throw new MissingMethodException("Could not locate the stock graph purchase-marker draw method or fields.");

            harmony.Patch(DrawPurchaseMarkersMethod,
                prefix: new HarmonyMethod(prefix),
                finalizer: new HarmonyMethod(finalizer));
        }

        private static void BeforeDrawPurchaseMarkers(UILineGraph __instance, object[] __args,
            out MarkerDrawState? __state)
        {
            __state = null;
            if (!Enabled || __instance == null || __args == null || __args.Length < 2 ||
                !( __args[1] is Rect drawRect))
                return;

            try
            {
                if (PurchaseMarkersField?.GetValue(__instance) is not IList markers ||
                    VisibleDataCountField == null || VisiblePointCountField == null || FirstVisibleTickField == null ||
                    MinimumPriceField == null || MaximumPriceField == null ||
                    MarkerRadiusField == null || MarkerStrokeWidthField == null ||
                    MarkerDataIndexField == null || MarkerPriceField == null || MarkerIsBuyField == null)
                    return;

                var visibleDataCount = (int)VisibleDataCountField.GetValue(__instance)!;
                var firstVisibleTick = (long)FirstVisibleTickField.GetValue(__instance)!;
                var minimumPrice = (float)MinimumPriceField.GetValue(__instance)!;
                var maximumPrice = (float)MaximumPriceField.GetValue(__instance)!;
                if (visibleDataCount < 1 || drawRect.height <= 0f || maximumPrice <= minimumPrice) return;

                var pricePerLocalUnit = (maximumPrice - minimumPrice) / drawRect.height;
                var markerRadius = (float)MarkerRadiusField.GetValue(__instance)!;
                var markerStroke = (float)MarkerStrokeWidthField.GetValue(__instance)!;
                var markerDiameter = markerRadius * 2f;
                if (pricePerLocalUnit <= 0f || markerDiameter <= 0f) return;

                var markerClearance = markerRadius + markerStroke + 1f;
                var minimumMarkerPrice = minimumPrice + (markerClearance * pricePerLocalUnit);
                var maximumMarkerPrice = maximumPrice - (markerClearance * pricePerLocalUnit);
                if (minimumMarkerPrice >= maximumMarkerPrice) return;

                var visibleLastTick = firstVisibleTick + visibleDataCount - 1L;
                var visibleDrawLastTick = firstVisibleTick + visibleDataCount + 4L;
                var visiblePointCount = (int)VisiblePointCountField!.GetValue(__instance)!;
                if (visiblePointCount < 2 || drawRect.width <= 0f) return;
                var xPerTick = drawRect.width / (visiblePointCount - 1);
                var candidates = new List<MarkerPoint>(markers.Count);
                for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    var marker = markers[markerIndex];
                    if (marker == null) continue;

                    var dataIndex = (long)MarkerDataIndexField.GetValue(marker)!;
                    if (dataIndex < firstVisibleTick || dataIndex > visibleDrawLastTick) continue;

                    var price = (float)MarkerPriceField.GetValue(marker)!;
                    // Match the native renderer: off-range markers are not part of the visible graph.
                    if (float.IsNaN(price) || float.IsInfinity(price) || price < minimumPrice || price > maximumPrice) continue;

                    // Match all spillover samples accepted and clamped by the native renderer.
                    var displayedTick = Math.Min(dataIndex, visibleLastTick);
                    candidates.Add(new MarkerPoint(markerIndex, displayedTick,
                        price, (bool)MarkerIsBuyField.GetValue(marker)!));
                }

                if (candidates.Count < 2) return;
                candidates.Sort((left, right) =>
                {
                    var byTick = left.DisplayedTick.CompareTo(right.DisplayedTick);
                    return byTick != 0 ? byTick : left.MarkerIndex.CompareTo(right.MarkerIndex);
                });

                var changes = CalculateVerticalOffsets(candidates, minimumMarkerPrice, maximumMarkerPrice,
                    (markerDiameter + markerStroke + 1f) * pricePerLocalUnit, xPerTick * pricePerLocalUnit);
                if (changes.Count == 0) return;

                __state = new MarkerDrawState(markers, changes);
                ApplyPriceChanges(markers, changes);
            }
            catch (Exception exception)
            {
                if (__state != null) RestorePrices(__state);
                __state = null;
                ReportFailure(exception);
            }
        }

        private static List<PriceChange> CalculateVerticalOffsets(List<MarkerPoint> candidates,
            float minimumPrice, float maximumPrice, float spacing, float horizontalScale)
        {
            var changes = new List<PriceChange>();
            var colliding = new bool[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
                for (var j = i + 1; j < candidates.Count; j++)
                {
                    var dx = (candidates[j].DisplayedTick - candidates[i].DisplayedTick) * horizontalScale;
                    if (candidates[j].DisplayedTick - candidates[i].DisplayedTick > ClusterWindowTicks) break;
                    var dy = candidates[j].Price - candidates[i].Price;
                    if (dx * dx + dy * dy >= spacing * spacing) continue;
                    colliding[i] = colliding[j] = true;
                }

            // Preserve isolated dots and treat them as obstacles for moved dots.
            var placed = new List<MarkerPoint>();
            for (var i = 0; i < candidates.Count; i++)
                if (!colliding[i]) placed.Add(candidates[i]);
            for (var i = 0; i < candidates.Count; i++)
            {
                if (!colliding[i]) continue;
                var marker = candidates[i];
                var bands = new List<VerticalBand>();
                foreach (var other in placed)
                {
                    var ticks = Math.Abs(marker.DisplayedTick - other.DisplayedTick);
                    var dx = ticks * horizontalScale;
                    if (ticks > ClusterWindowTicks || dx >= spacing) continue;
                    var clearance = (float)Math.Sqrt(spacing * spacing - dx * dx);
                    bands.Add(new VerticalBand(other.Price - clearance, other.Price + clearance));
                }
                bands.Sort((a, b) => a.Low.CompareTo(b.Low));
                var free = new List<VerticalBand>();
                var cursor = minimumPrice;
                foreach (var band in bands)
                {
                    if (band.High < cursor) continue;
                    if (band.Low > cursor) free.Add(new VerticalBand(cursor, Math.Min(band.Low, maximumPrice)));
                    cursor = Math.Max(cursor, band.High);
                    if (cursor > maximumPrice) break;
                }
                if (cursor <= maximumPrice) free.Add(new VerticalBand(cursor, maximumPrice));

                // IsBuy is authoritative, including CloseShort. Prefer buys below
                // execution and sells above it, with full circle clearance per lane.
                var desired = marker.Price + (marker.IsBuy ? -spacing / 2f : spacing / 2f);
                var selected = Mathf.Clamp(desired, minimumPrice, maximumPrice);
                var bestDistance = float.PositiveInfinity;
                var preferred = new List<VerticalBand>();
                foreach (var interval in free)
                {
                    var low = marker.IsBuy ? interval.Low : Math.Max(interval.Low, desired);
                    var high = marker.IsBuy ? Math.Min(interval.High, desired) : interval.High;
                    if (low <= high) preferred.Add(new VerticalBand(low, high));
                }
                foreach (var interval in preferred.Count > 0 ? preferred : free)
                {
                    var candidate = Mathf.Clamp(desired, interval.Low, interval.High);
                    var distance = Math.Abs(candidate - desired);
                    if (distance >= bestDistance) continue;
                    selected = candidate;
                    bestDistance = distance;
                }
                // If the plot cannot fit another circle, retain a visible dot.
                // No horizontal shift or data deletion is used to hide congestion.
                placed.Add(new MarkerPoint(marker.MarkerIndex, marker.DisplayedTick, selected, marker.IsBuy));
                if (!Mathf.Approximately(selected, marker.Price))
                    changes.Add(new PriceChange(marker.MarkerIndex, marker.Price, selected));
            }
            return changes;
        }

        private readonly struct VerticalBand
        {
            internal readonly float Low;
            internal readonly float High;
            internal VerticalBand(float low, float high) { Low = low; High = high; }
        }

        private static void ApplyPriceChanges(IList markers, List<PriceChange> changes)
        {
            if (MarkerPriceField == null) return;
            foreach (var change in changes)
            {
                var marker = markers[change.MarkerIndex];
                if (marker == null) continue;
                MarkerPriceField.SetValue(marker, change.DisplacedPrice);
                markers[change.MarkerIndex] = marker;
            }
        }

        private static Exception? RestoreAfterDraw(Exception? __exception, MarkerDrawState? __state)
        {
            if (__state != null) RestorePrices(__state);
            return __exception;
        }

        private static void RestorePrices(MarkerDrawState state)
        {
            if (MarkerPriceField == null) return;
            foreach (var change in state.PriceChanges)
            {
                if (change.MarkerIndex < 0 || change.MarkerIndex >= state.Markers.Count) continue;
                var marker = state.Markers[change.MarkerIndex];
                if (marker == null) continue;
                MarkerPriceField.SetValue(marker, change.OriginalPrice);
                state.Markers[change.MarkerIndex] = marker;
            }
        }

        private static void ReportFailure(Exception exception)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            Debug.LogWarning("Purchase-marker unstacking was skipped after an error: " + exception.Message);
        }

        private readonly struct MarkerPoint
        {
            internal readonly int MarkerIndex;
            internal readonly long DisplayedTick;
            internal readonly float Price;
            internal readonly bool IsBuy;

            internal MarkerPoint(int markerIndex, long displayedTick, float price, bool isBuy)
            {
                MarkerIndex = markerIndex;
                DisplayedTick = displayedTick;
                Price = price;
                IsBuy = isBuy;
            }
        }

        private readonly struct PriceChange
        {
            internal readonly int MarkerIndex;
            internal readonly float OriginalPrice;
            internal readonly float DisplacedPrice;

            internal PriceChange(int markerIndex, float originalPrice, float displacedPrice)
            {
                MarkerIndex = markerIndex;
                OriginalPrice = originalPrice;
                DisplacedPrice = displacedPrice;
            }
        }

        private sealed class MarkerDrawState
        {
            internal readonly IList Markers;
            internal readonly List<PriceChange> PriceChanges;

            internal MarkerDrawState(IList markers, List<PriceChange> priceChanges)
            {
                Markers = markers;
                PriceChanges = priceChanges;
            }
        }
    }
}
