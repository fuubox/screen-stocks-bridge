using System;
using System.Reflection;
using HarmonyLib;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal static class NewsTickerHarmonyPatch
    {
        internal static NewsTickerService? CaptureService;

        [ThreadStatic]
        private static NewsTickerItemDto? _pending;

        internal static void Install(Harmony harmony)
        {
            var ordinary = typeof(MarketNewsTickerUI).GetMethod("OrdinaryMessage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(MarketNewsEventDto) }, null);
            var scheduled = typeof(MarketNewsTickerUI).GetMethod("ScheduledMessage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(ScheduledPriceAnnouncementDto) }, null);
            var spawn = typeof(MarketNewsTickerUI).GetMethod("SpawnText",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string) }, null);
            var ordinaryPostfix = typeof(NewsTickerHarmonyPatch).GetMethod(nameof(AfterOrdinaryMessage),
                BindingFlags.Static | BindingFlags.NonPublic);
            var scheduledPostfix = typeof(NewsTickerHarmonyPatch).GetMethod(nameof(AfterScheduledMessage),
                BindingFlags.Static | BindingFlags.NonPublic);
            var spawnPostfix = typeof(NewsTickerHarmonyPatch).GetMethod(nameof(AfterSpawnText),
                BindingFlags.Static | BindingFlags.NonPublic);
            if (ordinary == null || scheduled == null || spawn == null || ordinaryPostfix == null ||
                scheduledPostfix == null || spawnPostfix == null)
                throw new MissingMethodException("Could not locate the news ticker rendering methods.");

            harmony.Patch(ordinary, postfix: new HarmonyMethod(ordinaryPostfix));
            harmony.Patch(scheduled, postfix: new HarmonyMethod(scheduledPostfix));
            harmony.Patch(spawn, postfix: new HarmonyMethod(spawnPostfix));
        }

        private static void AfterOrdinaryMessage(MarketNewsEventDto __0, string __result)
        {
            _pending = new NewsTickerItemDto
            {
                type = "market",
                text = __result ?? string.Empty,
                id = __0.id ?? string.Empty,
                cursor = __0.cursor ?? string.Empty,
                createdAtMs = __0.createdAtMs,
                stockId = __0.stockId ?? string.Empty,
                price = __0.price,
                kind = __0.kind ?? string.Empty,
                lookbackMinutes = __0.lookbackMinutes,
                debug = __0.debug
            };
        }

        private static void AfterScheduledMessage(ScheduledPriceAnnouncementDto __0, string __result)
        {
            _pending = new NewsTickerItemDto
            {
                type = "scheduled_price",
                text = __result ?? string.Empty,
                id = __0.id ?? string.Empty,
                cursor = __0.cursor ?? string.Empty,
                occurrenceId = __0.occurrenceId ?? string.Empty,
                revision = __0.revision ?? string.Empty,
                stockId = __0.stockId ?? string.Empty,
                targetPrice = __0.targetPrice,
                scheduledAtMs = __0.scheduledAtMs,
                reminderOffsetMs = __0.reminderOffsetMs,
                publishedAtMs = __0.publishedAtMs,
                direction = __0.direction ?? string.Empty
            };
        }

        private static void AfterSpawnText(string __0, string __1)
        {
            if (_pending == null) return;
            var pending = _pending;
            _pending = null;
            if (!string.Equals(pending.text, __0, StringComparison.Ordinal) ||
                !string.Equals(pending.stockId, __1, StringComparison.Ordinal)) return;
            CaptureService?.Capture(pending);
        }
    }
}
