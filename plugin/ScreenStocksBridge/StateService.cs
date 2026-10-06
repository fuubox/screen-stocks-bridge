using System;
using System.Globalization;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class StateService
    {
        private readonly AutoActionsService _autoActions = new AutoActionsService();

        internal StateSnapshotDto CreateSnapshot()
        {
            var snapshot = new StateSnapshotDto();
            var game = GameManager.I;
            var manager = StockManager.I;
            if (game == null || manager == null || game.Data == null || manager.Source == null || !manager.Source.IsReady)
                return snapshot;

            snapshot.ready = true;
            snapshot.serverTick = manager.Source.ServerTick;
            snapshot.cash = FormatBigNumber(game.Money);
            snapshot.level = game.PlayerLevel;
            snapshot.cooldowns = CreateTradeCooldowns(game);
            snapshot.autoActions = _autoActions.CreateSnapshot();
            var settings = game.Settings;
            if (settings != null && settings.allStocks != null)
            {
                foreach (var stock in settings.allStocks)
                {
                    if (stock == null || !game.IsStockVisibleToPlayer(stock)) continue;
                    var stockId = stock.stockId ?? string.Empty;
                    var price = manager.GetCurrentPrice(stockId);
                    var stockDto = new StockDto
                    {
                        stockId = stockId,
                        name = stock.stockName ?? stockId,
                        price = price,
                        unlocked = game.IsStockUnlocked(stock),
                        basePrice = stock.basePrice,
                        priceCap = stock.EffectivePriceCap,
                        dividendRate = stock.dividendRate,
                        maxVolume = stock.maxVolume,
                        effectiveMaxVolume = FormatBigNumber(manager.GetEffectiveMaxVolume(stockId)),
                        availableShares = (int)Math.Max(0, Math.Min(int.MaxValue, manager.GetAvailableShares(stockId).ToDouble()))
                    };
                    snapshot.stocks.Add(stockDto);
                }
            }

            if (game.Data.stockPositions == null) return snapshot;
            foreach (var position in game.Data.stockPositions)
            {
                if (position == null) continue;
                snapshot.positions.Add(new PositionDto
                {
                    stockId = position.stockId ?? string.Empty,
                    sharesOwned = FormatBigNumber(position.sharesOwned),
                    averageBuyPrice = position.averageBuyPrice.ToString("R", CultureInfo.InvariantCulture),
                    sharesShorted = FormatBigNumber(position.sharesShorted),
                    averageShortPrice = position.averageShortPrice.ToString("R", CultureInfo.InvariantCulture)
                });
            }
            return snapshot;
        }

        internal string SnapshotJson() => BridgeJson.SerializeSnapshot(CreateSnapshot());

        internal static string FormatBigNumber(BigNumber value)
        {
            const int scaleDigits = 6;
            var whole = value.RawValue;
            var wholeValue = BigNumber.Parse(whole.ToString(CultureInfo.InvariantCulture));
            var fraction = (value - wholeValue).ToDouble();
            var fractionUnits = (long)Math.Round(Math.Abs(fraction) * 1000000d, MidpointRounding.AwayFromZero);
            if (fractionUnits >= 1000000)
            {
                whole += fraction < 0d ? -1 : 1;
                fractionUnits = 0;
            }
            var wholeText = whole.ToString(CultureInfo.InvariantCulture);
            var negative = value.IsNegative;
            if (wholeText.StartsWith("-", StringComparison.Ordinal)) wholeText = wholeText.Substring(1);
            var fractionText = fractionUnits.ToString("D" + scaleDigits, CultureInfo.InvariantCulture).TrimEnd('0');
            var number = fractionText.Length == 0 ? wholeText : wholeText + "." + fractionText;
            return negative ? "-" + number : number;
        }

        private static TradeCooldownsDto CreateTradeCooldowns(GameManager game)
        {
            var now = ScreenStocks.Net.ServerClock.UnixSeconds;
            return new TradeCooldownsDto
            {
                serverNowUnixSeconds = now,
                serverClockSynchronized = ScreenStocks.Net.ServerClock.IsSynchronized,
                buy = CreateCooldown(game.Data.nextBuyTradeAvailableTimestamp, game.GetUpgradeValue(StatType.BuyCooldown), now),
                shortTrade = CreateCooldown(game.Data.nextShortTradeAvailableTimestamp, game.GetUpgradeValue(StatType.ShortCooldown), now)
            };
        }

        private static CooldownDto CreateCooldown(long availableAt, float configuredDuration, long now)
        {
            var remaining = Math.Max(0L, availableAt - now);
            return new CooldownDto
            {
                durationSeconds = Mathf.CeilToInt(Mathf.Max(0f, configuredDuration)),
                availableAtUnixSeconds = availableAt,
                remainingSeconds = remaining,
                active = remaining > 0
            };
        }
    }
}
