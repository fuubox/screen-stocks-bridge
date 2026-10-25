using System.Collections.Generic;

namespace ScreenStocksBridge
{
    internal sealed class StatsService
    {
        internal string Snapshot(string requestId)
        {
            var game = GameManager.I;
            if (game == null || game.Data == null)
                return ProtocolJson.Error(requestId, "not_ready", "Player stats are not available yet.");

            var data = game.Data;
            var snapshot = new StatsSnapshotDto
            {
                ready = true,
                netWorth = StateService.FormatBigNumber(game.CalculateNetWorth()),
                lifetimeBuyingProfit = StateService.FormatBigNumber(data.lifetimeBuyingProfit),
                lifetimeShortingProfit = StateService.FormatBigNumber(data.lifetimeShortingProfit),
                highestNetWorth = StateService.FormatBigNumber(data.highestNetWorth),
                totalPlaytime = data.totalPlaytime,
                highestRanking = data.highestRanking,
                totalBuys = data.totalBuys,
                totalSells = data.totalSells,
                totalShorts = data.totalShorts,
                totalCovers = data.totalCovers,
                currentXP = StateService.FormatBigNumber(data.currentXP),
                serverEffectiveCurrentXP = StateService.FormatBigNumber(data.serverEffectiveCurrentXP),
                totalXPGained = StateService.FormatBigNumber(data.totalXPGained),
                totalXPGainedEver = StateService.FormatBigNumber(data.totalXPGainedEver),
                totalMoneyEarned = StateService.FormatBigNumber(data.totalMoneyEarned),
                clan = data.clan.ToString(),
                ownedGenerators = data.ownedGenerators == null
                    ? new List<string>()
                    : new List<string>(data.ownedGenerators)
            };
            return ProtocolJson.Response(requestId, true, BridgeJson.SerializeStatsSnapshot(snapshot), string.Empty);
        }
    }
}
