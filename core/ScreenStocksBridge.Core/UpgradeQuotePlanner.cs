using System;

namespace ScreenStocksBridge
{
    // Money arithmetic stays in the caller's native representation (BigNumber in game).
    internal static class UpgradeQuotePlanner
    {
        internal static UpgradeQuotePlan<T> Plan<T>(int level, int maxLevel, int limit,
            int? quantity, Func<int, bool>? targetReached, bool useBudget, T budget, T zero,
            Func<int, T> priceAtLevel, Func<T, T, T> add, Func<T, T, int> compare)
        {
            if (level < 0 || maxLevel < level || limit < 1 || limit > 5000 || quantity < 0)
                throw new ArgumentOutOfRangeException();
            var result = new UpgradeQuotePlan<T> { TotalCost = zero, LevelAfter = level };
            while (true)
            {
                if (targetReached != null && targetReached(result.LevelAfter)) { result.StopReason = "target_reached"; break; }
                if (quantity.HasValue && result.Quantity >= quantity.Value) { result.StopReason = "quantity_reached"; break; }
                if (result.LevelAfter >= maxLevel) { result.StopReason = "maxed"; break; }
                if (result.Quantity >= limit) { result.StopReason = "calculation_limit"; break; }
                var price = priceAtLevel(result.LevelAfter);
                if (compare(price, zero) < 0) throw new InvalidOperationException("The game returned a negative upgrade price.");
                var total = add(result.TotalCost, price);
                if (useBudget && compare(total, budget) > 0) { result.StopReason = "budget_exhausted"; break; }
                result.TotalCost = total;
                result.Quantity++;
                result.LevelAfter++;
            }
            return result;
        }
    }

    internal sealed class UpgradeQuotePlan<T>
    {
        internal int Quantity;
        internal int LevelAfter;
        internal T TotalCost = default!;
        internal string StopReason = string.Empty;
    }
}
