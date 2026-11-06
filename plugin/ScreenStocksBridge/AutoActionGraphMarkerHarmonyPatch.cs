using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using ScreenStocks.Net;
using HarmonyLib;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal static class AutoActionGraphMarkerHarmonyPatch
    {
        private const int MaximumTrackedMarkers = 128;
        private const float TrackedMarkerLifetimeSeconds = 180f;
        private const int MinimumVisiblePointCount = 20;

        private static readonly Dictionary<ActionMarkerKey, MarkerCapture> PredictedMarkers =
            new Dictionary<ActionMarkerKey, MarkerCapture>();
        private static readonly Dictionary<ActionMarkerKey, MarkerCapture> ServerMarkers =
            new Dictionary<ActionMarkerKey, MarkerCapture>();
        private static bool _reportedFailure;
        private static ConditionalWeakTable<RemoteMarketDataSource, SourceAnchor> SourceAnchors =
            new ConditionalWeakTable<RemoteMarketDataSource, SourceAnchor>();

        private static readonly MethodInfo? GetMarkersMethod = typeof(StockManager).GetMethod("GetMarkers",
            BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
        private static readonly FieldInfo? MarkerDataIndexField = GetMarkerField("DataIndex");
        private static readonly FieldInfo? MarkerPriceField = GetMarkerField("Price");
        private static readonly FieldInfo? MarkerIsBuyField = GetMarkerField("IsBuy");
        private static readonly FieldInfo? MarkerIsShortField = GetMarkerField("IsShort");

        internal static void Install(Harmony harmony)
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var managerFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var addMarker = typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarker", managerFlags | BindingFlags.Static,
                null, new[] { typeof(AutoAction), typeof(float?) }, null);
            var reconcileMarkers = typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarkers", managerFlags,
                null, new[] { typeof(IReadOnlyList<AutoAction>), typeof(IReadOnlyList<AutoAction>) }, null);
            var applyPrices = typeof(RemoteMarketDataSource).GetMethod("ApplyPollResult", managerFlags,
                null, new[] { typeof(ApiResult<PricesResponse>) }, null);
            var beforePrices = typeof(AutoActionGraphMarkerHarmonyPatch).GetMethod(nameof(BeforeApplyPrices), flags);
            var beforeAddMarker = typeof(AutoActionGraphMarkerHarmonyPatch).GetMethod(
                nameof(BeforeAddRemoteExecutionMarker), flags);
            var afterAddMarker = typeof(AutoActionGraphMarkerHarmonyPatch).GetMethod(
                nameof(AfterAddRemoteExecutionMarker), flags);
            var afterReconcile = typeof(AutoActionGraphMarkerHarmonyPatch).GetMethod(
                nameof(AfterRemoteExecutionMarkers), flags);

            if (applyPrices == null || beforePrices == null || addMarker == null || reconcileMarkers == null || beforeAddMarker == null ||
                afterAddMarker == null || afterReconcile == null || GetMarkersMethod == null ||
                MarkerDataIndexField == null || MarkerPriceField == null ||
                MarkerIsBuyField == null || MarkerIsShortField == null)
                throw new MissingMethodException("Could not locate the auto-action graph marker methods or fields.");

            harmony.Patch(applyPrices, prefix: new HarmonyMethod(beforePrices));
            harmony.Patch(addMarker,
                prefix: new HarmonyMethod(beforeAddMarker),
                postfix: new HarmonyMethod(afterAddMarker));
            harmony.Patch(reconcileMarkers, postfix: new HarmonyMethod(afterReconcile));
        }

        internal static void Clear()
        {
            PredictedMarkers.Clear();
            ServerMarkers.Clear();
            _reportedFailure = false;
            SourceAnchors = new ConditionalWeakTable<RemoteMarketDataSource, SourceAnchor>();
        }

        private static void BeforeAddRemoteExecutionMarker(AutoAction __0, float? __1,
            out MarkerWriteState? __state)
        {
            __state = null;
            try
            {
                if (__0 == null || string.IsNullOrWhiteSpace(__0.stockId)) return;

                var markers = GetMarkerList(__0.stockId);
                if (markers == null) return;

                __state = new MarkerWriteState(markers, markers.Count, new ActionSettings(__0), __1.HasValue);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }

        private static void AfterAddRemoteExecutionMarker(AutoAction __0, float? __1,
            MarkerWriteState? __state)
        {
            try
            {
                if (__0 == null || __state == null) return;
                PruneTrackedMarkers();

                var markers = GetMarkerList(__state.Settings.StockId);
                if (!ReferenceEquals(markers, __state.Markers) || markers == null ||
                    markers.Count <= __state.CountBefore)
                    return;

                var markerIndex = markers.Count - 1;
                if (!TryReadMarker(markers, markerIndex, out var marker)) return;

                var capture = new MarkerCapture(markers, markerIndex, marker, __state.Settings,
                    Time.realtimeSinceStartup);
                var key = new ActionMarkerKey(__state.Settings.StockId, __0.slotIndex, __0.lastExecutionMarker);
                Store(__state.IsPrediction ? PredictedMarkers : ServerMarkers, key, capture);
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }

        private static void AfterRemoteExecutionMarkers(IReadOnlyList<AutoAction> __0,
            IReadOnlyList<AutoAction> __1)
        {
            try
            {
                if (__0 == null || __1 == null) return;
                PruneTrackedMarkers();

                for (var currentIndex = 0; currentIndex < __1.Count; currentIndex++)
                {
                    var current = __1[currentIndex];
                    if (current == null) continue;

                    var previous = FindBySlot(__0, current.slotIndex);
                    if (previous == null) continue;
                    if (current.lastExecutionMarker <= previous.lastExecutionMarker)
                        continue;
                    if (string.IsNullOrWhiteSpace(current.stockId)) continue;

                    var predictionKey = new ActionMarkerKey(current.stockId, current.slotIndex,
                        previous.lastExecutionMarker);
                    var serverKey = new ActionMarkerKey(current.stockId, current.slotIndex,
                        current.lastExecutionMarker);
                    MarkerCapture? capture = Take(PredictedMarkers, predictionKey);
                    if (capture == null) capture = Take(ServerMarkers, serverKey);
                    if (capture == null) continue;

                    if (TryFindLogicalPoint(capture, current.lastExecutionMarker,
                            out var dataIndex, out var price))
                        TryWriteMarker(capture, dataIndex, price);
                }
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }

        private static void ReportFailure(Exception exception)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            Debug.LogWarning("Auto-action graph marker correction was skipped after an error: " + exception.Message);
        }

        private static AutoAction? FindBySlot(IReadOnlyList<AutoAction> actions, int slotIndex)
        {
            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                if (action != null && action.slotIndex == slotIndex) return action;
            }
            return null;
        }

        private static bool TryFindLogicalPoint(MarkerCapture capture, long executionTimestamp,
            out long dataIndex, out float price)
        {
            dataIndex = capture.Original.DataIndex;
            price = capture.Original.Price;

            var settings = capture.Settings;
            if (string.IsNullOrWhiteSpace(settings.StockId) || !IsFinitePositive(settings.TargetPrice))
                return false;

            var stockManager = StockManager.I;
            if (stockManager == null) return false;

            var history = stockManager.GetPriceHistory(settings.StockId);
            if (history == null || history.Count == 0) return false;

            var totalSamples = stockManager.GetTotalTickCount(settings.StockId);
            if (totalSamples <= 0) return false;

            if (!(stockManager.Source is RemoteMarketDataSource remoteSource))
                return false;

            var historyCount = history.Count;
            var firstGlobalIndex = Math.Max(0L, totalSamples - historyCount + 1L);
            var visibleCount = GetVisiblePointCount(historyCount);
            var firstVisibleIndex = Math.Max(0, historyCount - visibleCount);
            var sampleTicks = remoteSource.GetHistorySampleTicks(settings.StockId);
            if (sampleTicks == null || sampleTicks.Count != historyCount) return false;

            var gameSettings = GameManager.I != null ? GameManager.I.Settings : null;
            if (executionTimestamp <= 0 || gameSettings == null || gameSettings.tickRateMs <= 0 ||
                !SourceAnchors.TryGetValue(remoteSource, out var anchor) ||
                anchor.TimeMs <= 0 || anchor.Tick != remoteSource.ServerTick)
                return false;

            // A received quote tick and its timestamp form one anchor. The
            // continuously advancing clock must never be paired with a stale tick.
            var latestServerTick = anchor.Tick;
            var eventServerTick = latestServerTick +
                ((double)executionTimestamp - anchor.TimeMs) / gameSettings.tickRateMs;
            var searchEnd = FindLastServerSample(sampleTicks, latestServerTick);
            if (searchEnd <= firstVisibleIndex) return false;

            var isAbove = settings.Condition == ActionCondition.Above;
            if (!TryFindEventSampleBracket(sampleTicks, firstVisibleIndex, searchEnd, eventServerTick,
                    out var lowerIndex, out var upperIndex)) return false;

            var localIndex = FindNearestEventSample(history, sampleTicks, lowerIndex, upperIndex,
                eventServerTick, settings.TargetPrice, isAbove);
            if (localIndex < lowerIndex || localIndex > upperIndex || !IsFinitePositive(history[localIndex]))
                return false;

            dataIndex = firstGlobalIndex + localIndex;
            price = history[localIndex];
            return true;
        }

        private static void BeforeApplyPrices(RemoteMarketDataSource __instance, ApiResult<PricesResponse> __0)
        {
            // Mirror native admission before callbacks can reconcile actions. The
            // mapping below additionally requires ServerTick to have advanced to
            // this anchor, so evidence is unusable until the game accepts it.
            if (!__0.Success || __0.Data == null ||
                (__instance.IsReady && __0.Data.tick < __instance.ServerTick)) return;
            // A successful response without time invalidates any older pairing.
            var anchor = SourceAnchors.GetValue(__instance, _ => new SourceAnchor());
            anchor.Tick = __0.Data.tick;
            anchor.TimeMs = __0.Data.serverTimeMs;
        }

        private sealed class SourceAnchor
        {
            internal long Tick;
            internal long TimeMs;
        }

        private static int FindLastServerSample(IReadOnlyList<long> sampleTicks, long latestServerTick)
        {
            for (var index = sampleTicks.Count - 1; index >= 0; index--)
                if (sampleTicks[index] <= latestServerTick)
                    return index + 1;
            return 0;
        }

        private static bool TryFindEventSampleBracket(IReadOnlyList<long> sampleTicks, int startIndex,
            int endExclusive, double eventServerTick, out int lowerIndex, out int upperIndex)
        {
            lowerIndex = -1;
            upperIndex = -1;
            for (var index = startIndex; index < endExclusive; index++)
            {
                var tick = sampleTicks[index];
                if (tick <= eventServerTick) lowerIndex = index;
                if (tick >= eventServerTick)
                {
                    upperIndex = index;
                    break;
                }
            }

            if (lowerIndex >= 0 && upperIndex >= 0) return true;

            if (lowerIndex < 0 && upperIndex >= 0)
            {
                var interval = upperIndex + 1 < endExclusive
                    ? sampleTicks[upperIndex + 1] - sampleTicks[upperIndex]
                    : upperIndex > startIndex ? sampleTicks[upperIndex] - sampleTicks[upperIndex - 1] : 0L;
                if (interval <= 0L || sampleTicks[upperIndex] - eventServerTick > interval) return false;
                lowerIndex = upperIndex;
                return true;
            }

            if (upperIndex < 0 && lowerIndex >= startIndex)
            {
                var interval = lowerIndex > startIndex
                    ? sampleTicks[lowerIndex] - sampleTicks[lowerIndex - 1]
                    : lowerIndex + 1 < endExclusive ? sampleTicks[lowerIndex + 1] - sampleTicks[lowerIndex] : 0L;
                if (interval <= 0L || eventServerTick - sampleTicks[lowerIndex] > interval) return false;
                upperIndex = lowerIndex;
                return true;
            }

            return false;
        }

        private static int FindNearestEventSample(IReadOnlyList<float> history,
            IReadOnlyList<long> sampleTicks, int lowerIndex, int upperIndex, double eventServerTick,
            float targetPrice, bool isAbove)
        {
            if (lowerIndex == upperIndex)
                return IsFinitePositive(history[lowerIndex]) ? lowerIndex : -1;

            var lowerDistance = Math.Abs(eventServerTick - sampleTicks[lowerIndex]);
            var upperDistance = Math.Abs(eventServerTick - sampleTicks[upperIndex]);
            if (lowerDistance < upperDistance)
                return IsFinitePositive(history[lowerIndex]) ? lowerIndex : upperIndex;
            if (upperDistance < lowerDistance)
                return IsFinitePositive(history[upperIndex]) ? upperIndex : lowerIndex;

            return FindClosestPoint(history, lowerIndex, upperIndex + 1, targetPrice, isAbove);
        }

        private static int FindClosestPoint(IReadOnlyList<float> history, int startIndex, int endExclusive,
            float targetPrice, bool isAbove)
        {
            var closestIndex = -1;
            var closestDistance = double.PositiveInfinity;
            var hasConditionMatch = false;

            for (var index = startIndex; index < endExclusive; index++)
            {
                var candidatePrice = history[index];
                if (!IsFinitePositive(candidatePrice)) continue;

                var matchesCondition = IsConditionMet(candidatePrice, targetPrice, isAbove);
                if (matchesCondition && !hasConditionMatch)
                {
                    hasConditionMatch = true;
                    closestIndex = -1;
                    closestDistance = double.PositiveInfinity;
                }
                if (hasConditionMatch && !matchesCondition) continue;

                var distance = Math.Abs((double)candidatePrice - targetPrice);
                if (distance > closestDistance) continue;

                closestIndex = index;
                closestDistance = distance;
            }

            return closestIndex;
        }

        private static bool IsConditionMet(float price, float targetPrice, bool isAbove)
        {
            return isAbove ? price >= targetPrice : price <= targetPrice;
        }

        private static int GetVisiblePointCount(int historyCount)
        {
            var settings = GameManager.I != null ? GameManager.I.Settings : null;
            if (settings == null) return historyCount;

            var maxVisiblePoints = Math.Max(MinimumVisiblePointCount, settings.historyLength);
            return Mathf.Clamp(settings.chartVisibleTicks, MinimumVisiblePointCount, maxVisiblePoints);
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static IList? GetMarkerList(string stockId)
        {
            var manager = StockManager.I;
            if (manager == null || GetMarkersMethod == null) return null;
            return GetMarkersMethod.Invoke(manager, new object[] { stockId }) as IList;
        }

        private static bool TryReadMarker(IList markers, int index, out MarkerValue marker)
        {
            marker = default;
            if (index < 0 || index >= markers.Count) return false;

            var value = markers[index];
            if (value == null || MarkerDataIndexField == null || MarkerPriceField == null ||
                MarkerIsBuyField == null || MarkerIsShortField == null)
                return false;

            marker = new MarkerValue(
                (long)MarkerDataIndexField.GetValue(value)!,
                (float)MarkerPriceField.GetValue(value)!,
                (bool)MarkerIsBuyField.GetValue(value)!,
                (bool)MarkerIsShortField.GetValue(value)!);
            return true;
        }

        private static bool TryWriteMarker(MarkerCapture capture, long dataIndex, float price)
        {
            if (capture.MarkerIndex < 0 || capture.MarkerIndex >= capture.Markers.Count ||
                !TryReadMarker(capture.Markers, capture.MarkerIndex, out var current) ||
                !capture.Original.Equals(current) ||
                !MarkerMatchesAction(capture.Settings.ActionType, current) ||
                MarkerDataIndexField == null || MarkerPriceField == null)
                return false;

            var value = capture.Markers[capture.MarkerIndex];
            if (value == null) return false;

            MarkerDataIndexField.SetValue(value, dataIndex);
            MarkerPriceField.SetValue(value, price);
            capture.Markers[capture.MarkerIndex] = value;
            return true;
        }

        private static bool MarkerMatchesAction(AutoActionType actionType, MarkerValue marker)
        {
            switch (actionType)
            {
                case AutoActionType.Buy:
                    return marker.IsBuy && !marker.IsShort;
                case AutoActionType.Short:
                    return !marker.IsBuy && marker.IsShort;
                case AutoActionType.CloseBuy:
                    return !marker.IsBuy && !marker.IsShort;
                case AutoActionType.CloseShort:
                    return marker.IsBuy && marker.IsShort;
                default:
                    return false;
            }
        }

        private static FieldInfo? GetMarkerField(string name)
        {
            var markerType = GetMarkersMethod?.ReturnType.GetGenericArguments()[0];
            return markerType?.GetField(name, BindingFlags.Instance | BindingFlags.Public);
        }

        private static MarkerCapture? Take(Dictionary<ActionMarkerKey, MarkerCapture> markers,
            ActionMarkerKey key)
        {
            if (!markers.TryGetValue(key, out var capture)) return null;
            markers.Remove(key);
            return capture;
        }

        private static void Store(Dictionary<ActionMarkerKey, MarkerCapture> markers,
            ActionMarkerKey key, MarkerCapture capture)
        {
            markers[key] = capture;
            while (markers.Count > MaximumTrackedMarkers)
            {
                var hasOldest = false;
                var oldestKey = default(ActionMarkerKey);
                var oldestTime = float.MaxValue;
                foreach (var pair in markers)
                {
                    if (pair.Value.CapturedAt >= oldestTime) continue;
                    hasOldest = true;
                    oldestTime = pair.Value.CapturedAt;
                    oldestKey = pair.Key;
                }
                if (!hasOldest) break;
                markers.Remove(oldestKey);
            }
        }

        private static void PruneTrackedMarkers()
        {
            var now = Time.realtimeSinceStartup;
            Prune(PredictedMarkers, now);
            Prune(ServerMarkers, now);
        }

        private static void Prune(Dictionary<ActionMarkerKey, MarkerCapture> markers, float now)
        {
            var expired = new List<ActionMarkerKey>();
            foreach (var pair in markers)
                if (now - pair.Value.CapturedAt >= TrackedMarkerLifetimeSeconds)
                    expired.Add(pair.Key);
            for (var index = 0; index < expired.Count; index++) markers.Remove(expired[index]);
        }

        private readonly struct ActionSettings
        {
            internal readonly string StockId;
            internal readonly ActionCondition Condition;
            internal readonly AutoActionType ActionType;
            internal readonly float TargetPrice;

            internal ActionSettings(AutoAction action)
            {
                StockId = action.stockId ?? string.Empty;
                Condition = action.condition;
                ActionType = action.actionType;
                TargetPrice = action.targetPrice;
            }
        }

        private readonly struct ActionMarkerKey : IEquatable<ActionMarkerKey>
        {
            private readonly string _stockId;
            private readonly int _slotIndex;
            private readonly long _executionMarker;

            internal ActionMarkerKey(string stockId, int slotIndex, long executionMarker)
            {
                _stockId = stockId ?? string.Empty;
                _slotIndex = slotIndex;
                _executionMarker = executionMarker;
            }

            public bool Equals(ActionMarkerKey other)
            {
                return _slotIndex == other._slotIndex && _executionMarker == other._executionMarker &&
                       string.Equals(_stockId, other._stockId, StringComparison.Ordinal);
            }

            public override bool Equals(object? obj) => obj is ActionMarkerKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(_stockId);
                    hash = (hash * 397) ^ _slotIndex;
                    return (hash * 397) ^ _executionMarker.GetHashCode();
                }
            }
        }

        private sealed class MarkerWriteState
        {
            internal readonly IList Markers;
            internal readonly int CountBefore;
            internal readonly ActionSettings Settings;
            internal readonly bool IsPrediction;

            internal MarkerWriteState(IList markers, int countBefore, ActionSettings settings,
                bool isPrediction)
            {
                Markers = markers;
                CountBefore = countBefore;
                Settings = settings;
                IsPrediction = isPrediction;
            }
        }

        private sealed class MarkerCapture
        {
            internal readonly IList Markers;
            internal readonly int MarkerIndex;
            internal readonly MarkerValue Original;
            internal readonly ActionSettings Settings;
            internal readonly float CapturedAt;

            internal MarkerCapture(IList markers, int markerIndex, MarkerValue original,
                ActionSettings settings, float capturedAt)
            {
                Markers = markers;
                MarkerIndex = markerIndex;
                Original = original;
                Settings = settings;
                CapturedAt = capturedAt;
            }
        }

        private readonly struct MarkerValue : IEquatable<MarkerValue>
        {
            internal readonly long DataIndex;
            internal readonly float Price;
            internal readonly bool IsBuy;
            internal readonly bool IsShort;

            internal MarkerValue(long dataIndex, float price, bool isBuy, bool isShort)
            {
                DataIndex = dataIndex;
                Price = price;
                IsBuy = isBuy;
                IsShort = isShort;
            }

            public bool Equals(MarkerValue other)
            {
                return DataIndex == other.DataIndex && Price.Equals(other.Price) &&
                       IsBuy == other.IsBuy && IsShort == other.IsShort;
            }
        }
    }
}
