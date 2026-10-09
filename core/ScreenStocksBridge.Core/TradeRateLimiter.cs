using System;

namespace ScreenStocksBridge
{
    internal sealed class TradeRateLimiter
    {
        private const double Capacity = 10d;
        private const double TokensPerSecond = 1d;
        private double _tokens = Capacity;
        private double _lastUpdatedAtSeconds;

        internal bool TryConsume(int cost, double nowSeconds, out int retryAfterMs)
        {
            retryAfterMs = 0;
            if (cost <= 0 || cost > Capacity || double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
                return false;

            if (nowSeconds > _lastUpdatedAtSeconds)
            {
                _tokens = Math.Min(Capacity, _tokens + (nowSeconds - _lastUpdatedAtSeconds) * TokensPerSecond);
                _lastUpdatedAtSeconds = nowSeconds;
            }

            if (_tokens < cost)
            {
                var retryMilliseconds = Math.Ceiling((cost - _tokens) / TokensPerSecond * 1000d);
                retryAfterMs = retryMilliseconds >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)retryMilliseconds);
                return false;
            }

            _tokens -= cost;
            return true;
        }
    }

    internal static class TradeRatePolicy
    {
        internal static bool TryGetTokenCost(string action, bool hasLongPosition,
            bool hasShortPosition, out int cost)
        {
            cost = 0;
            switch (action)
            {
                case "buy_max":
                case "buy_percent":
                    cost = hasShortPosition ? 2 : 1;
                    return true;
                case "short_max":
                case "short_percent":
                    cost = hasLongPosition ? 2 : 1;
                    return true;
                case "sell_max":
                case "sell_percent":
                case "cover_max":
                case "cover_percent":
                    cost = 1;
                    return true;
                case "close_max":
                case "close_percent":
                    cost = (hasLongPosition ? 1 : 0) + (hasShortPosition ? 1 : 0);
                    return cost > 0;
                default:
                    return false;
            }
        }
    }
}
