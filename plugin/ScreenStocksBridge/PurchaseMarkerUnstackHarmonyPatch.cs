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
        private static readonly FieldInfo? MarkerIsBuyField = PurchaseMarkerType?.GetField(
            "IsBuy", BindingFlags.Instance | BindingFlags.Public);
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
                MarkerIsBuyField == null || MarkerIsShortField == null || prefix == null || finalizer == null)
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
                    MarkerDataIndexField == null || MarkerPriceField == null || MarkerIsBuyField == null ||
                    MarkerIsShortField == null)
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
                var visibleDrawLastTick = visibleLastTick + 4L;
                var candidates = new List<MarkerPoint>(markers.Count);
                for (var markerIndex = 0; markerIndex < markers.Count; markerIndex++)
                {
                    var marker = markers[markerIndex];
                    if (marker == null) continue;

                    var dataIndex = (long)MarkerDataIndexField.GetValue(marker)!;
                    if (dataIndex < firstVisibleTick || dataIndex > visibleDrawLastTick) continue;

                    var price = (float)MarkerPriceField.GetValue(marker)!;
                    // Match the native renderer: off-range markers are not part of the visible graph.
                    if (price < minimumPrice || price > maximumPrice) continue;

                    // The native renderer maps its final four accepted ticks onto the last visible x position.
                    var displayedTick = Math.Min(dataIndex, visibleLastTick);
                    candidates.Add(new MarkerPoint(markerIndex, displayedTick,
                        price, (bool)MarkerIsBuyField.GetValue(marker)!,
                        (bool)MarkerIsShortField.GetValue(marker)!));
                }

                if (candidates.Count < 2) return;
                candidates = ResolveXStackedActionSides(candidates);
                candidates.Sort((left, right) =>
                {
                    var byTick = left.DisplayedTick.CompareTo(right.DisplayedTick);
                    return byTick != 0 ? byTick : left.MarkerIndex.CompareTo(right.MarkerIndex);
                });

                var changes = CalculateVerticalOffsets(candidates, minimumMarkerPrice, maximumMarkerPrice,
                    markerDiameter * pricePerLocalUnit);
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
            float minimumPrice, float maximumPrice, float maximumPriceOffset)
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
                        maximumPriceOffset, changes);

                firstCandidate = afterCluster;
            }

            return changes;
        }

        private static void AddClusterOffsets(List<MarkerPoint> candidates, int start, int end,
            float minimumPrice, float maximumPrice, float maximumPriceOffset, List<PriceChange> changes)
        {
            var sellMarkers = new List<MarkerPoint>();
            var buyMarkers = new List<MarkerPoint>();
            for (var index = start; index < end; index++)
            {
                var marker = candidates[index];
                if (marker.IsBuySide) buyMarkers.Add(marker);
                else sellMarkers.Add(marker);
            }

            sellMarkers.Sort((left, right) =>
            {
                var byPrice = left.Price.CompareTo(right.Price);
                return byPrice != 0 ? byPrice : left.MarkerIndex.CompareTo(right.MarkerIndex);
            });
            buyMarkers.Sort((left, right) =>
            {
                var byPrice = left.Price.CompareTo(right.Price);
                return byPrice != 0 ? byPrice : left.MarkerIndex.CompareTo(right.MarkerIndex);
            });

            // IsBuy represents trade side: a short cover is buy-side, while a long close is sell-side.
            // A lone marker on either side stays put; opposite-side overlap is not a collision.
            if (sellMarkers.Count > 1)
            {
                OrderPairedMarkers(sellMarkers, buySide: false);
                for (var index = 0; index < sellMarkers.Count; index++)
                {
                    var fraction = (index + 1f) / sellMarkers.Count;
                    AddOffset(sellMarkers[index], fraction * maximumPriceOffset,
                        minimumPrice, maximumPrice, changes);
                }
            }

            if (buyMarkers.Count > 1)
            {
                OrderPairedMarkers(buyMarkers, buySide: true);
                for (var index = 0; index < buyMarkers.Count; index++)
                {
                    var fraction = (buyMarkers.Count - index) / (float)buyMarkers.Count;
                    AddOffset(buyMarkers[index], -fraction * maximumPriceOffset,
                        minimumPrice, maximumPrice, changes);
                }
            }
        }

        private static List<MarkerPoint> ResolveXStackedActionSides(List<MarkerPoint> candidates)
        {
            var resolved = new List<MarkerPoint>(candidates.Count);
            for (var index = 0; index < candidates.Count; index++)
            {
                var marker = candidates[index];
                if (marker.IsBuy && marker.IsShort &&
                    TryFindXStackedCounterpart(candidates, index, out var counterpart))
                {
                    // A dual-flag marker is ambiguous until its x-aligned action is present:
                    // with a short it is a sell; with a buy it is a short cover.
                    marker = marker.WithResolvedSide(counterpart.IsBuy, counterpart.MarkerIndex);
                }
                resolved.Add(marker);
            }

            return resolved;
        }

        private static bool TryFindXStackedCounterpart(List<MarkerPoint> candidates, int markerIndex,
            out MarkerPoint counterpart)
        {
            var marker = candidates[markerIndex];
            counterpart = default;
            var found = false;
            var closestPriceDistance = double.PositiveInfinity;
            for (var index = 0; index < candidates.Count; index++)
            {
                if (index == markerIndex) continue;
                var candidate = candidates[index];
                if (candidate.DisplayedTick != marker.DisplayedTick || candidate.IsBuy == candidate.IsShort)
                    continue;

                var priceDistance = Math.Abs((double)candidate.Price - marker.Price);
                if (priceDistance > closestPriceDistance ||
                    (priceDistance == closestPriceDistance && found && candidate.MarkerIndex > counterpart.MarkerIndex))
                    continue;

                counterpart = candidate;
                closestPriceDistance = priceDistance;
                found = true;
            }

            return found;
        }

        private static void OrderPairedMarkers(List<MarkerPoint> markers, bool buySide)
        {
            for (var index = 0; index < markers.Count; index++)
            {
                var marker = markers[index];
                if (marker.PairedMarkerIndex < 0) continue;

                var counterpartIndex = -1;
                for (var candidateIndex = 0; candidateIndex < markers.Count; candidateIndex++)
                {
                    if (markers[candidateIndex].MarkerIndex != marker.PairedMarkerIndex) continue;
                    counterpartIndex = candidateIndex;
                    break;
                }
                if (counterpartIndex < 0) continue;

                // Buy-side cover markers go below their paired buy; sell-side markers go above their short.
                var pairShouldComeFirst = buySide;
                if ((pairShouldComeFirst && index > counterpartIndex) ||
                    (!pairShouldComeFirst && index < counterpartIndex))
                {
                    var paired = markers[index];
                    markers[index] = markers[counterpartIndex];
                    markers[counterpartIndex] = paired;
                }
            }
        }

        private static void AddOffset(MarkerPoint marker, float desiredOffset, float minimumPrice,
            float maximumPrice, List<PriceChange> changes)
        {
            var boundedPrice = Mathf.Clamp(marker.Price + desiredOffset, minimumPrice, maximumPrice);
            if ((desiredOffset > 0f && boundedPrice <= marker.Price) ||
                (desiredOffset < 0f && boundedPrice >= marker.Price))
                return;
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
            internal readonly bool IsBuy;
            internal readonly bool IsShort;
            internal readonly bool IsBuySide;
            internal readonly int PairedMarkerIndex;

            internal MarkerPoint(int markerIndex, long displayedTick, float price, bool isBuy, bool isShort)
            {
                MarkerIndex = markerIndex;
                DisplayedTick = displayedTick;
                Price = price;
                IsBuy = isBuy;
                IsShort = isShort;
                IsBuySide = isBuy;
                PairedMarkerIndex = -1;
            }

            private MarkerPoint(int markerIndex, long displayedTick, float price, bool isBuy, bool isShort,
                bool isBuySide, int pairedMarkerIndex)
            {
                MarkerIndex = markerIndex;
                DisplayedTick = displayedTick;
                Price = price;
                IsBuy = isBuy;
                IsShort = isShort;
                IsBuySide = isBuySide;
                PairedMarkerIndex = pairedMarkerIndex;
            }

            internal MarkerPoint WithResolvedSide(bool isBuySide, int pairedMarkerIndex) =>
                new MarkerPoint(MarkerIndex, DisplayedTick, Price, IsBuy, IsShort, isBuySide, pairedMarkerIndex);
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
