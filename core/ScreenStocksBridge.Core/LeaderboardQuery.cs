using System;

namespace ScreenStocksBridge
{
    internal sealed class LeaderboardQuery
    {
        internal string mode = string.Empty;
        internal int radius;
        internal int count;
        internal bool isClan;
        internal string cacheKey = string.Empty;

        internal static bool TryParse(RequestParams? parameters, out LeaderboardQuery? query,
            out string errorCode, out string errorMessage)
        {
            query = null;
            errorCode = string.Empty;
            errorMessage = string.Empty;
            var mode = (parameters?.mode ?? string.Empty).Trim().ToLowerInvariant();
            var parsed = new LeaderboardQuery { mode = mode };
            if (mode == "current" || mode == "all_time" || mode == "ipo")
            {
                var radius = parameters?.radius ?? 0;
                if (radius < 0 || radius > 100)
                    return Fail("invalid_radius", "radius must be between 1 and 100.", out errorCode, out errorMessage);
                parsed.radius = radius == 0 ? 10 : radius;
                parsed.cacheKey = mode + ":" + parsed.radius.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (mode == "current_top")
            {
                var count = parameters?.count ?? 0;
                if (count < 0 || count > 100)
                    return Fail("invalid_count", "count must be between 1 and 100.", out errorCode, out errorMessage);
                parsed.count = count == 0 ? 100 : count;
                parsed.cacheKey = mode + ":" + parsed.count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (mode == "clan_net_worth" || mode == "clan_player_share")
            {
                parsed.isClan = true;
                parsed.cacheKey = mode;
            }
            else
            {
                return Fail("invalid_mode", "mode must be current, all_time, ipo, current_top, clan_net_worth, or clan_player_share.", out errorCode, out errorMessage);
            }

            query = parsed;
            return true;
        }

        private static bool Fail(string code, string message, out string errorCode, out string errorMessage)
        {
            errorCode = code;
            errorMessage = message;
            return false;
        }
    }
}
