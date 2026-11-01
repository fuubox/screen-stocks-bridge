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
        private static readonly FieldInfo? MarkerIsShortField = PurchaseMarkerType?.GetField(
            "IsShort", BindingFlags.Instance | BindingFlags.Public);

        private static bool _reportedFailure;

        internal static bool Enabled { get; set; }

        internal static void Install(Harmony harmony)
        {
            var prefix = typeof(PurchaseMarkerUnstackHarmonyPatch).GetMethod(
                nameof(BeforeDrawPurchaseMarkers), BindingFlags.Static | BindingFlags.NonPublic);
            var finalizer = typeof(PurchaseMarkerUnstackHarmonyPatch).GetMethod(
                nameof(RestoreAfterDraw), BindingFlags.Static | BindingFlags.NonPublic);

            if (DrawPurchaseMarkersMethod == null || PurchaseMarkersField == null ||
                VisibleDataCountField == null || FirstVisibleTickField == null ||
                MinimumPriceField == null || MaximumPriceField == null ||
                MarkerRadiusField == null || MarkerStrokeWidthField == null ||
                MarkerDataIndexField == null || MarkerPriceField == null ||
                MarkerIsShortField == null || prefix == null || finalizer == null)
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
                    VisibleDataCountField == null || FirstVisibleTickField == null ||
                    MinimumPriceField == null || MaximumPriceField == null ||
                    MarkerRadiusField == null || MarkerStrokeWidthField == null ||
                    MarkerDataIndexField == null || MarkerPriceField == null || MarkerIsShortField == null)
                    return;

                var visibleDataCount = (int)VisibleDataCountField.GetValue(__instance)!;
                var firstVisibleTick = (long)FirstVisibleTickField.GetValue(__instance)!;
                var minimumPrice = (float)MinimumPriceField.GetValue(__instance)!;
                var maximumPrice = (float)MaximumPriceField.GetValue(__instance)!;
                if (visibleDataCount < 1 || drawRect.height <= 0f || maximumPrice <= minimumPrice) return;

                var pricePerLocalUnit = (maximumPrice - minimumPrice) / drawRect.height;
                var markerRadius = (float)MarkerRadiusField.GetValue(__instance)!;
                var markerStroke = (float)MarkerStrokeWidthField.GetValue(__instance)!;
                var verticalGap = (markerRadius * 2f) + markerStroke + 2f;
                if (pricePerLocalUnit <= 0f || verticalGap <= 0f) return;

                var visibleLastTick = firstVisibleTick + visibleDataCount - 1L;
                var visibleDrawLastTick = visibleLastTick + 4L;
                var candidates = new List<MarkerPoint>(markers.Count);
                for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    var marker = markers[markerIndex];
                    if (marker == null) continue;

                    var dataIndex = (long)MarkerDataIndexField.GetValue(marker)!;
                    if (dataIndex < firstVisibleTick || dataIndex > visibleDrawLastTick) continue;

                    // The native renderer maps its final four accepted ticks onto the last visible x position.
                    var displayedTick = Math.Min(dataIndex, visibleLastTick);
                    candidates.Add(new MarkerPoint(markerIndex, displayedTick,
                        (float)MarkerPriceField.GetValue(marker)!, (bool)MarkerIsShortField.GetValue(marker)!));
                }

                if (candidates.Count < 2) return;
                candidates.Sort((left, right) =>
                {
                    var byTick = left.DisplayedTick.CompareTo(right.DisplayedTick);
                    return byTick != 0 ? byTick : left.MarkerIndex.CompareTo(right.MarkerIndex);
                });

                var changes = CalculateVerticalOffsets(candidates, minimumPrice, maximumPrice,
                    verticalGap * pricePerLocalUnit);
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
            float minimumPrice, float maximumPrice, float priceGap)
        {
            var changes = new List<PriceChange>();
            var firstCandidate = 0;
            while (firstCandidate < candidates.Count)
            {
                var afterCluster = firstCandidate + 1;
                while (afterCluster < candidates.Count &&
                       candidates[afterCluster].DisplayedTick - candidates[afterCluster - 1].DisplayedTick <= ClusterWindowTicks)
                    afterCluster++;

                var clusterCount = afterCluster - firstCandidate;
                if (clusterCount > 1)
                    AddClusterOffsets(candidates, firstCandidate, afterCluster, minimumPrice, maximumPrice,
                        priceGap, changes);

                firstCandidate = afterCluster;
            }

            return changes;
        }

        private static void AddClusterOffsets(List<MarkerPoint> candidates, int start, int end,
            float minimumPrice, float maximumPrice, float priceGap, List<PriceChange> changes)
        {
            var shortMarkers = new List<MarkerPoint>();
            var buyMarkers = new List<MarkerPoint>();
            for (var index = start; index < end; index++)
            {
                var marker = candidates[index];
                if (marker.IsShort) shortMarkers.Add(marker);
                else buyMarkers.Add(marker);
            }

            shortMarkers.Sort((left, right) =>
            {
                var byPrice = left.Price.CompareTo(right.Price);
                return byPrice != 0 ? byPrice : left.MarkerIndex.CompareTo(right.MarkerIndex);
            });
            buyMarkers.Sort((left, right) =>
            {
                var byPrice = left.Price.CompareTo(right.Price);
                return byPrice != 0 ? byPrice : left.MarkerIndex.CompareTo(right.MarkerIndex);
            });

            // IsShort groups both opening shorts and covers; non-short groups buys and sells that close buys.
            for (var index = 0; index < shortMarkers.Count; index++)
                AddOffset(shortMarkers[index], (index + 1) * priceGap, minimumPrice, maximumPrice, changes);

            for (var index = 0; index < buyMarkers.Count; index++)
                AddOffset(buyMarkers[index], -(buyMarkers.Count - index) * priceGap,
                    minimumPrice, maximumPrice, changes);
        }

        private static void AddOffset(MarkerPoint marker, float desiredOffset, float minimumPrice,
            float maximumPrice, List<PriceChange> changes)
        {
            var boundedPrice = Mathf.Clamp(marker.Price + desiredOffset, minimumPrice, maximumPrice);
            if (Mathf.Approximately(boundedPrice, marker.Price)) return;
            changes.Add(new PriceChange(marker.MarkerIndex, marker.Price, boundedPrice));
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
            internal readonly bool IsShort;

            internal MarkerPoint(int markerIndex, long displayedTick, float price, bool isShort)
            {
                MarkerIndex = markerIndex;
                DisplayedTick = displayedTick;
                Price = price;
                IsShort = isShort;
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
