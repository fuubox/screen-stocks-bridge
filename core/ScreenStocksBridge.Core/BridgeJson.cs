using System;
using System.Globalization;
using System.Text;

namespace ScreenStocksBridge
{
    internal static class BridgeJson
    {
        internal static string SerializeSnapshot(StateSnapshotDto value)
        {
            var json = new StringBuilder(1024);
            json.Append("{\"ready\":").Append(Bool(value.ready))
                .Append(",\"serverTick\":").Append(value.serverTick.ToString(CultureInfo.InvariantCulture))
                .Append(",\"cash\":").Append(Quote(value.cash))
                .Append(",\"netWorth\":").Append(Quote(value.netWorth))
                .Append(",\"level\":").Append(value.level.ToString(CultureInfo.InvariantCulture))
                .Append(",\"stocks\":[");
            for (var i = 0; i < value.stocks.Count; i++)
            {
                if (i > 0) json.Append(',');
                AppendStock(json, value.stocks[i]);
            }
            json.Append("],\"positions\":[");
            for (var i = 0; i < value.positions.Count; i++)
            {
                if (i > 0) json.Append(',');
                AppendPosition(json, value.positions[i]);
            }
            json.Append("],\"cooldowns\":");
            AppendCooldowns(json, value.cooldowns);
            json.Append(",\"autoActions\":");
            AppendAutoActions(json, value.autoActions);
            return json.Append('}').ToString();
        }

        internal static string SerializeAutoActions(AutoActionsSnapshotDto value)
        {
            var json = new StringBuilder(512);
            AppendAutoActions(json, value);
            return json.ToString();
        }

        internal static string SerializeHumanActivityPage(HumanActivityPageDto value)
        {
            var json = new StringBuilder(512);
            json.Append("{\"stockId\":").Append(Quote(value.stockId))
                .Append(",\"fullScaleImpact\":")
                .Append(value.fullScaleImpact.HasValue ? Number(value.fullScaleImpact.Value) : "null")
                .Append(",\"samples\":[");
            for (var i = 0; i < value.samples.Count; i++)
            {
                if (i > 0) json.Append(',');
                var sample = value.samples[i];
                json.Append("{\"sampleTick\":").Append(sample.sampleTick.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"up\":").Append(sample.up.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"down\":").Append(sample.down.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"total\":").Append(sample.total.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"net\":").Append(sample.net.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"upImpact\":").Append(Number(sample.upImpact))
                    .Append(",\"downImpact\":").Append(Number(sample.downImpact))
                    .Append(",\"totalImpact\":").Append(Number(sample.totalImpact))
                    .Append(",\"netImpact\":").Append(Number(sample.netImpact)).Append('}');
            }
            return json.Append("],\"hasMore\":").Append(Bool(value.hasMore))
                .Append(",\"nextBeforeTick\":").Append(value.nextBeforeTick.ToString(CultureInfo.InvariantCulture))
                .Append('}').ToString();
        }

        internal static string SerializeHumanActivityFocus(HumanActivityFocusDto value)
        {
            return "{\"active\":" + Bool(value.active) + ",\"stockId\":" + Quote(value.stockId ?? string.Empty) + "}";
        }

        internal static string SerializeNewsTickerItem(NewsTickerItemDto value)
        {
            var json = new StringBuilder(384);
            json.Append("{\"type\":").Append(Quote(value.type ?? string.Empty))
                .Append(",\"text\":").Append(Quote(value.text ?? string.Empty));
            if (value.type == "market")
            {
                json.Append(",\"id\":").Append(Quote(value.id ?? string.Empty))
                    .Append(",\"cursor\":").Append(Quote(value.cursor ?? string.Empty))
                    .Append(",\"createdAtMs\":").Append(value.createdAtMs.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"stockId\":").Append(Quote(value.stockId ?? string.Empty))
                    .Append(",\"price\":").Append(Number(value.price))
                    .Append(",\"kind\":").Append(Quote(value.kind ?? string.Empty))
                    .Append(",\"lookbackMinutes\":").Append(Number(value.lookbackMinutes))
                    .Append(",\"debug\":").Append(Bool(value.debug));
            }
            else if (value.type == "scheduled_price")
            {
                json.Append(",\"id\":").Append(Quote(value.id ?? string.Empty))
                    .Append(",\"cursor\":").Append(Quote(value.cursor ?? string.Empty))
                    .Append(",\"occurrenceId\":").Append(Quote(value.occurrenceId ?? string.Empty))
                    .Append(",\"revision\":").Append(Quote(value.revision ?? string.Empty))
                    .Append(",\"stockId\":").Append(Quote(value.stockId ?? string.Empty))
                    .Append(",\"targetPrice\":").Append(Number(value.targetPrice))
                    .Append(",\"scheduledAtMs\":").Append(value.scheduledAtMs.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"reminderOffsetMs\":").Append(value.reminderOffsetMs.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"publishedAtMs\":").Append(value.publishedAtMs.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"direction\":").Append(Quote(value.direction ?? string.Empty));
            }
            return json.Append('}').ToString();
        }

        internal static string SerializeAutoActionToast(AutoActionToastDto value)
        {
            var json = new StringBuilder(256);
            return json.Append("{\"text\":").Append(Quote(value.text ?? string.Empty))
                .Append(",\"stockId\":").Append(Quote(value.stockId ?? string.Empty))
                .Append(",\"actionType\":").Append(Quote(value.actionType ?? string.Empty))
                .Append(",\"condition\":").Append(Quote(value.condition ?? string.Empty))
                .Append(",\"targetPrice\":").Append(Number(value.targetPrice))
                .Append('}').ToString();
        }

        internal static string SerializeOfflineProgressSnapshot(OfflineProgressSnapshotDto value)
        {
            if (!value.available || value.summary == null) return "{\"available\":false,\"summary\":null}";

            var summary = value.summary;
            var json = new StringBuilder(512);
            json.Append("{\"available\":true,\"summary\":{\"total\":").Append(Quote(summary.total))
                .Append(",\"generators\":").Append(Quote(summary.generators))
                .Append(",\"dividends\":").Append(Quote(summary.dividends))
                .Append(",\"autoActions\":").Append(Quote(summary.autoActions))
                .Append(",\"secondsAway\":").Append(Number(summary.secondsAway))
                .Append(",\"cappedEarningsSeconds\":").Append(Number(summary.cappedEarningsSeconds))
                .Append(",\"showEarnings\":").Append(Bool(summary.showEarnings))
                .Append(",\"positionChanges\":[");
            for (var i = 0; i < summary.positionChanges.Count; i++)
            {
                if (i > 0) json.Append(',');
                var change = summary.positionChanges[i];
                json.Append("{\"stockId\":").Append(Quote(change.stockId))
                    .Append(",\"isLong\":").Append(Bool(change.isLong))
                    .Append(",\"cashChange\":").Append(Quote(change.cashChange))
                    .Append(",\"percentChange\":").Append(Number(change.percentChange)).Append('}');
            }
            return json.Append("]}}").ToString();
        }

        internal static string SerializeTransactionHistory(TransactionHistorySnapshotDto value)
        {
            var json = new StringBuilder(512).Append("{\"filter\":").Append(Quote(value.filter))
                .Append(",\"limit\":").Append(value.limit.ToString(CultureInfo.InvariantCulture))
                .Append(",\"cached\":").Append(Bool(value.cached))
                .Append(",\"stale\":").Append(Bool(value.stale))
                .Append(",\"ageSeconds\":").Append(value.ageSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"fetchedAtUnixSeconds\":").Append(value.fetchedAtUnixSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"retryAfterMs\":").Append(value.retryAfterMs.ToString(CultureInfo.InvariantCulture))
                .Append(",\"entries\":[");
            for (var i = 0; i < value.entries.Count; i++)
            {
                if (i > 0) json.Append(',');
                var entry = value.entries[i];
                json.Append("{\"id\":").Append(Quote(entry.id))
                    .Append(",\"occurredAt\":").Append(Quote(entry.occurredAt))
                    .Append(",\"stockId\":").Append(Quote(entry.stockId))
                    .Append(",\"side\":").Append(Quote(entry.side))
                    .Append(",\"source\":").Append(Quote(entry.source))
                    .Append(",\"realizedReturn\":").Append(Quote(entry.realizedReturn))
                    .Append(",\"realizedPercent\":").Append(Number(entry.realizedPercent)).Append('}');
            }
            return json.Append("]}").ToString();
        }

        internal static string SerializeNetWorthHistory(NetWorthHistorySnapshotDto value)
        {
            var json = new StringBuilder(512).Append("{\"range\":").Append(Quote(value.range))
                .Append(",\"intervalMinutes\":").Append(value.intervalMinutes.ToString(CultureInfo.InvariantCulture))
                .Append(",\"cached\":").Append(Bool(value.cached))
                .Append(",\"stale\":").Append(Bool(value.stale))
                .Append(",\"ageSeconds\":").Append(value.ageSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"fetchedAtUnixSeconds\":").Append(value.fetchedAtUnixSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"retryAfterMs\":").Append(value.retryAfterMs.ToString(CultureInfo.InvariantCulture))
                .Append(",\"samples\":[");
            for (var i = 0; i < value.samples.Count; i++)
            {
                if (i > 0) json.Append(',');
                var sample = value.samples[i];
                json.Append("{\"at\":").Append(Quote(sample.at))
                    .Append(",\"netWorth\":").Append(Number(sample.netWorth)).Append('}');
            }
            return json.Append("]}").ToString();
        }

        internal static string SerializeUpgrades(UpgradesSnapshotDto value)
        {
            var json = new StringBuilder(512).Append("{\"ready\":").Append(Bool(value.ready)).Append(",\"upgrades\":[");
            for (var i = 0; i < value.upgrades.Count; i++)
            {
                if (i > 0) json.Append(',');
                var upgrade = value.upgrades[i];
                json.Append("{\"upgradeId\":").Append(Quote(upgrade.upgradeId))
                    .Append(",\"displayName\":").Append(Quote(upgrade.displayName))
                    .Append(",\"description\":").Append(Quote(upgrade.description))
                    .Append(",\"hidden\":").Append(Bool(upgrade.hidden))
                    .Append(",\"currentLevel\":").Append(upgrade.currentLevel.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"hasMaxLevel\":").Append(Bool(upgrade.hasMaxLevel))
                    .Append(",\"maxLevel\":").Append(upgrade.maxLevel.HasValue ? upgrade.maxLevel.Value.ToString(CultureInfo.InvariantCulture) : "null")
                    .Append(",\"remainingLevels\":").Append(upgrade.remainingLevels.HasValue ? upgrade.remainingLevels.Value.ToString(CultureInfo.InvariantCulture) : "null")
                    .Append(",\"maxed\":").Append(Bool(upgrade.maxed))
                    .Append(",\"currentValue\":").Append(Number(upgrade.currentValue))
                    .Append(",\"nextValue\":").Append(upgrade.nextValue.HasValue ? Number(upgrade.nextValue.Value) : "null")
                    .Append(",\"nextPrice\":").Append(upgrade.nextPrice == null ? "null" : Quote(upgrade.nextPrice)).Append('}');
            }
            return json.Append("]}").ToString();
        }

        internal static string SerializeLeaderboardSnapshot(LeaderboardSnapshotDto value, bool cached)
        {
            var json = new StringBuilder(512).Append("{\"mode\":").Append(Quote(value.mode))
                .Append(",\"cached\":").Append(Bool(cached))
                .Append(",\"fetchedAtUnixSeconds\":").Append(value.fetchedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));
            if (value.isClan)
            {
                json.Append(",\"entries\":[");
                for (var i = 0; i < value.clans.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    var entry = value.clans[i];
                    json.Append("{\"rank\":").Append(entry.rank.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"clan\":").Append(Quote(entry.clan))
                        .Append(",\"netWorth\":").Append(Quote(entry.netWorth))
                        .Append(",\"playerPercentage\":").Append(Number(entry.playerPercentage)).Append('}');
                }
            }
            else
            {
                json.Append(",\"totalRanked\":").Append(value.totalRanked.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"selfRank\":").Append(value.selfRank.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"entries\":[");
                for (var i = 0; i < value.players.Count; i++)
                {
                    if (i > 0) json.Append(',');
                    var entry = value.players[i];
                    json.Append("{\"rank\":").Append(entry.rank.ToString(CultureInfo.InvariantCulture))
                        .Append(",\"steamId\":").Append(Quote(entry.steamId))
                        .Append(",\"displayName\":").Append(Quote(entry.displayName))
                        .Append(",\"clan\":").Append(Quote(entry.clan))
                        .Append(",\"netWorth\":").Append(Quote(entry.netWorth))
                        .Append(",\"ipoCount\":").Append(entry.ipoCount.ToString(CultureInfo.InvariantCulture)).Append('}');
                }
            }
            return json.Append("]}").ToString();
        }

        internal static string SerializeTradeCompleted(string requestId, string action, string stockId, string status, string reason)
        {
            return "{\"requestId\":" + Quote(requestId) + ",\"action\":" + Quote(action) +
                ",\"stockId\":" + Quote(stockId) + ",\"status\":" + Quote(status) +
                ",\"reason\":" + Quote(reason) + "}";
        }

        private static void AppendStock(StringBuilder json, StockDto value)
        {
            json.Append("{\"stockId\":").Append(Quote(value.stockId))
                .Append(",\"name\":").Append(Quote(value.name))
                .Append(",\"price\":").Append(Number(value.price))
                .Append(",\"unlocked\":").Append(Bool(value.unlocked))
                .Append(",\"basePrice\":").Append(Number(value.basePrice))
                .Append(",\"priceCap\":").Append(Number(value.priceCap))
                .Append(",\"dividendRate\":").Append(Number(value.dividendRate))
                .Append(",\"maxVolume\":").Append(value.maxVolume.ToString(CultureInfo.InvariantCulture))
                .Append(",\"effectiveMaxVolume\":").Append(Quote(value.effectiveMaxVolume))
                .Append(",\"availableShares\":").Append(value.availableShares.ToString(CultureInfo.InvariantCulture)).Append('}');
        }

        private static void AppendPosition(StringBuilder json, PositionDto value)
        {
            json.Append("{\"stockId\":").Append(Quote(value.stockId))
                .Append(",\"sharesOwned\":").Append(Quote(value.sharesOwned))
                .Append(",\"averageBuyPrice\":").Append(Quote(value.averageBuyPrice))
                .Append(",\"sharesShorted\":").Append(Quote(value.sharesShorted))
                .Append(",\"averageShortPrice\":").Append(Quote(value.averageShortPrice)).Append('}');
        }

        private static void AppendCooldowns(StringBuilder json, TradeCooldownsDto value)
        {
            json.Append("{\"serverNowUnixSeconds\":").Append(value.serverNowUnixSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"serverClockSynchronized\":").Append(Bool(value.serverClockSynchronized))
                .Append(",\"buy\":");
            AppendCooldown(json, value.buy);
            json.Append(",\"shortTrade\":");
            AppendCooldown(json, value.shortTrade);
            json.Append('}');
        }

        private static void AppendCooldown(StringBuilder json, CooldownDto value)
        {
            json.Append("{\"durationSeconds\":").Append(value.durationSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"availableAtUnixSeconds\":").Append(value.availableAtUnixSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"remainingSeconds\":").Append(value.remainingSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"active\":").Append(Bool(value.active)).Append('}');
        }

        private static void AppendAutoActions(StringBuilder json, AutoActionsSnapshotDto value)
        {
            json.Append("{\"ready\":").Append(Bool(value.ready))
                .Append(",\"unlocked\":").Append(Bool(value.unlocked))
                .Append(",\"active\":").Append(Bool(value.active))
                .Append(",\"slotLimit\":").Append(value.slotLimit.ToString(CultureInfo.InvariantCulture))
                .Append(",\"configuredCount\":").Append(value.configuredCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"canAddAction\":").Append(Bool(value.canAddAction))
                .Append(",\"cooldownDurationSeconds\":").Append(value.cooldownDurationSeconds.ToString(CultureInfo.InvariantCulture))
                .Append(",\"actions\":[");
            for (var i = 0; i < value.actions.Count; i++)
            {
                if (i > 0) json.Append(',');
                var action = value.actions[i];
                json.Append("{\"slotIndex\":").Append(action.slotIndex.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"stockId\":").Append(Quote(action.stockId))
                    .Append(",\"actionType\":").Append(Quote(action.actionType))
                    .Append(",\"condition\":").Append(Quote(action.condition))
                    .Append(",\"targetPrice\":").Append(Number(action.targetPrice))
                    .Append(",\"amountPercentage\":").Append(action.amountPercentage.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"enabled\":").Append(Bool(action.enabled))
                    .Append(",\"cooldownUntilUnixSeconds\":").Append(action.cooldownUntilUnixSeconds.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"cooldownDurationSeconds\":").Append(action.cooldownDurationSeconds.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"cooldownRemainingSeconds\":").Append(action.cooldownRemainingSeconds.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"onCooldown\":").Append(Bool(action.onCooldown)).Append('}');
            }
            json.Append("]}");
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string Number(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? "null" : value.ToString("R", CultureInfo.InvariantCulture);

        private static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value)
            ? "null" : value.ToString("R", CultureInfo.InvariantCulture);

        internal static string Quote(string value)
        {
            var json = new StringBuilder(value.Length + 2).Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (character < 0x20) json.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(character);
                        break;
                }
            }
            return json.Append('"').ToString();
        }
    }
}
