using System;
using System.Text.RegularExpressions;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal static class UpgradeQuoteService
    {
        internal static string Handle(BridgeRequest request, GameManager game,
            PlayerUpgradesSO.StatData definition, StatType stat)
        {
            var args = request.@params!;
            var mode = string.IsNullOrEmpty(args.mode) ? "quantity" : args.mode;
            if (args.maxLevels < 1 || args.maxLevels > 5000)
                return ProtocolJson.Error(request.id, "invalid_limit", "maxLevels must be between 1 and 5000.");
            if (mode != "quantity" && mode != "budget" && mode != "cash_fraction" && mode != "target_level" && mode != "target_value")
                return ProtocolJson.Error(request.id, "invalid_mode", "Expected quantity, budget, cash_fraction, target_level, or target_value.");
            if (mode == "quantity" && args.quantity < 1)
                return ProtocolJson.Error(request.id, "invalid_quantity", "quantity must be a positive integer.");
            if (mode == "target_level" && args.targetLevel < 0)
                return ProtocolJson.Error(request.id, "invalid_target", "targetLevel must be nonnegative.");
            if (mode == "target_value" && !Finite(args.targetValue))
                return ProtocolJson.Error(request.id, "invalid_target", "targetValue must be finite.");
            if (mode == "cash_fraction" && (!Finite(args.cashFraction) || args.cashFraction <= 0d || args.cashFraction > 1d))
                return ProtocolJson.Error(request.id, "invalid_budget", "cashFraction must be greater than zero and at most one.");
            if (mode == "budget" && (args.budget == null || args.budget.Length > 256 ||
                !Regex.IsMatch(args.budget, @"\A[0-9]+(?:\.[0-9]{1,6})?\z")))
                return ProtocolJson.Error(request.id, "invalid_budget", "budget must be a nonnegative decimal string with at most six fractional digits (no exponent or suffix).");

            try
            {
                var cash = game.Money;
                var useBudget = mode == "budget" || mode == "cash_fraction";
                var budget = mode == "budget" ? BigNumber.Parse(args.budget) : BigNumber.Zero;
                if (mode == "cash_fraction")
                {
                    // Round down to millionths, then perform the money operation without doubles.
                    var millionths = (long)Math.Floor(args.cashFraction * 1000000d);
                    if (millionths < 1)
                        return ProtocolJson.Error(request.id, "invalid_budget", "cashFraction must be at least 0.000001.");
                    budget = BigNumber.MulDiv(cash, millionths, 1000000, false);
                }
                if (budget < BigNumber.Zero)
                    return ProtocolJson.Error(request.id, "quote_unavailable", "Available cash cannot be negative for a budget quote.");

                var level = Math.Max(0, game.GetUpgradeLevel(stat));
                var maxLevel = Math.Max(level, game.GetUpgradeMaxLevelForBuy(stat));
                if (game.IsUpgradeMaxed(stat)) maxLevel = level;
                var current = game.GetUpgradeValue(stat);
                var remote = ScreenStocksAuthManager.I?.Upgrades;
                var view = remote != null && remote.HasData ? remote.Get(stat) : null;
                var basePrice = view != null ? view.basePrice : definition.basePrice;
                var priceMultiplier = view != null ? view.priceMultiplier : definition.priceMultiplier;
                if (!Finite(basePrice) || !Finite(priceMultiplier) || basePrice < 0f || priceMultiplier < 0f)
                    throw new InvalidOperationException("The upgrade price formula is invalid.");
                // Reward and IPO bonuses are additive. Keep their observed contribution fixed.
                Func<int, double> baseValue = view != null
                    ? new Func<int, double>(n => (float)(view.baseValue + (float)n * view.valuePerLevel))
                    : n => game.Upgrades.GetValue(stat, n);
                var baseBefore = baseValue(level);
                Func<int, double> valueAt = n => n == level ? current : (float)(current + baseValue(n) - baseBefore);
                var direction = view != null ? view.valuePerLevel : definition.valuePerLevel;
                if (!Finite(current) || !Finite(baseBefore) || !Finite(direction))
                    throw new InvalidOperationException("The upgrade value formula is not finite.");

                Func<int, bool>? targetReached = null;
                if (mode == "target_level") targetReached = n => n >= args.targetLevel;
                if (mode == "target_value")
                {
                    if (direction == 0f && args.targetValue != current)
                        return ProtocolJson.Error(request.id, "unreachable_target", "This upgrade does not change its value per level.");
                    targetReached = n => direction < 0f ? valueAt(n) <= args.targetValue : valueAt(n) >= args.targetValue;
                }
                var plan = UpgradeQuotePlanner.Plan(level, maxLevel, args.maxLevels,
                    mode == "quantity" ? (int?)args.quantity : null, targetReached, useBudget, budget,
                    BigNumber.Zero, n => game.GetUpgradePriceAtLevel(stat, n),
                    (a, b) => a + b, (a, b) => a.CompareTo(b));
                var resultingValue = valueAt(plan.LevelAfter);
                if (!Finite(resultingValue)) throw new InvalidOperationException("The resulting upgrade value is not finite.");
                var quote = new UpgradeQuoteDto
                {
                    upgradeId = stat.ToString(), mode = mode, quantity = plan.Quantity,
                    levelBefore = level, levelAfter = plan.LevelAfter, currentValue = current,
                    resultingValue = resultingValue, totalCost = StateService.FormatBigNumber(plan.TotalCost),
                    cashBefore = StateService.FormatBigNumber(cash),
                    remainingCash = StateService.FormatBigNumber(cash - plan.TotalCost),
                    budget = useBudget ? StateService.FormatBigNumber(budget) : null,
                    affordable = plan.TotalCost <= cash, capReached = plan.LevelAfter >= maxLevel,
                    truncated = plan.StopReason == "calculation_limit",
                    targetReached = targetReached == null ? (bool?)null : targetReached(plan.LevelAfter),
                    calculationLimit = args.maxLevels, stopReason = plan.StopReason,
                    valueSource = view == null ? "local_catalog" : "remote_catalog",
                };
                return ProtocolJson.Response(request.id, true, BridgeJson.SerializeUpgradeQuote(quote), string.Empty);
            }
            catch (Exception ex)
            {
                return ProtocolJson.Error(request.id, "quote_unavailable", ex.Message);
            }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
