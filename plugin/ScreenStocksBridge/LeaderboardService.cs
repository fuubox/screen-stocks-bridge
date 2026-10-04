using System;
using System.Diagnostics;
using System.Threading.Tasks;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal sealed class LeaderboardService
    {
        private readonly Plugin _plugin;
        private readonly LeaderboardRequestCache<LeaderboardSnapshotDto> _cache =
            new LeaderboardRequestCache<LeaderboardSnapshotDto>();
        private PendingRequest? _pending;

        internal LeaderboardService(Plugin plugin) { _plugin = plugin; }

        internal void Handle(BridgeRequest request, BridgeConnection connection)
        {
            if (!LeaderboardQuery.TryParse(request.@params, out var query, out var errorCode, out var errorMessage))
            {
                connection.Send(ProtocolJson.Error(request.id, errorCode, errorMessage), false);
                return;
            }

            var now = NowMilliseconds();
            if (_cache.TryGetCached(query!.cacheKey, now, out var cached))
            {
                connection.Send(ProtocolJson.Response(request.id, true,
                    BridgeJson.SerializeLeaderboardSnapshot(cached, true), string.Empty), false);
                return;
            }

            var client = ScreenStocksAuthManager.I?.Leaderboard;
            if (client == null)
            {
                connection.Send(ProtocolJson.Error(request.id, "not_ready", "The game's leaderboard client is not ready."), false);
                return;
            }

            var start = _cache.TryBeginFetch(now, out var retryAfterMs);
            if (start == LeaderboardRequestStart.InFlight)
            {
                connection.Send(ProtocolJson.Error(request.id, "leaderboard_busy",
                    "A leaderboard request is already in progress.", retryAfterMs), false);
                return;
            }
            if (start == LeaderboardRequestStart.RateLimited)
            {
                connection.Send(ProtocolJson.Error(request.id, "rate_limited",
                    "Leaderboard requests are limited to one per 30 seconds.", retryAfterMs), false);
                return;
            }

            try
            {
                Task<WireLeaderboardResponse>? playerTask = null;
                Task<WireClanLeaderboardResponse>? clanTask = null;
                var mode = ToGameMode(query.mode);
                if (query.isClan) clanTask = client.GetClansAsync(mode);
                else if (query.mode == "current_top") playerTask = client.GetTopAsync(query.count);
                else playerTask = client.GetAroundMeAsync(query.radius, mode);

                if (query.isClan)
                    _pending = new PendingRequest(request.id, connection, query, clanTask!);
                else
                    _pending = new PendingRequest(request.id, connection, query, playerTask!);
            }
            catch (Exception ex)
            {
                _cache.FailFetch();
                _plugin.LogWarning("The game's leaderboard client rejected a request: " + ex.Message);
                connection.Send(ProtocolJson.Error(request.id, "leaderboard_unavailable",
                    "The game could not start the leaderboard request."), false);
            }
        }

        internal void Update()
        {
            var pending = _pending;
            if (pending == null || !pending.IsCompleted) return;
            _pending = null;

            try
            {
                var snapshot = pending.GetSnapshot();
                snapshot.fetchedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _cache.CompleteFetch(pending.query.cacheKey, snapshot, NowMilliseconds());
                if (!pending.connection.IsClosed)
                    pending.connection.Send(ProtocolJson.Response(pending.requestId, true,
                        BridgeJson.SerializeLeaderboardSnapshot(snapshot, false), string.Empty), false);
            }
            catch (Exception ex)
            {
                _cache.FailFetch();
                _plugin.LogWarning("The game's leaderboard request failed: " + ex.Message);
                if (!pending.connection.IsClosed)
                    pending.connection.Send(ProtocolJson.Error(pending.requestId, "leaderboard_unavailable",
                        "The game could not retrieve the leaderboard."), false);
            }
        }

        private static LeaderboardSnapshotDto ConvertPlayer(LeaderboardQuery query, WireLeaderboardResponse response)
        {
            var snapshot = new LeaderboardSnapshotDto
            {
                mode = query.mode,
                totalRanked = response.totalRanked,
                selfRank = response.selfRank,
            };
            if (response.entries == null) return snapshot;
            foreach (var entry in response.entries)
            {
                if (entry == null) continue;
                snapshot.players.Add(new LeaderboardPlayerEntryDto
                {
                    rank = entry.rank,
                    steamId = entry.steamId ?? string.Empty,
                    displayName = entry.displayName ?? string.Empty,
                    clan = entry.clan ?? string.Empty,
                    netWorth = entry.netWorth.ToString(),
                    ipoCount = entry.ipoCount,
                });
            }
            return snapshot;
        }

        private static LeaderboardSnapshotDto ConvertClan(LeaderboardQuery query, WireClanLeaderboardResponse response)
        {
            var snapshot = new LeaderboardSnapshotDto { mode = query.mode, isClan = true };
            if (response.entries == null) return snapshot;
            foreach (var entry in response.entries)
            {
                if (entry == null) continue;
                snapshot.clans.Add(new LeaderboardClanEntryDto
                {
                    rank = entry.rank,
                    clan = entry.clan ?? string.Empty,
                    netWorth = entry.netWorth.ToString(),
                    playerPercentage = entry.playerPercentage,
                });
            }
            return snapshot;
        }

        private static LeaderboardMode ToGameMode(string mode)
        {
            switch (mode)
            {
                case "current": return LeaderboardMode.Current;
                case "all_time": return LeaderboardMode.AllTime;
                case "ipo": return LeaderboardMode.Ipo;
                case "current_top": return LeaderboardMode.CurrentTop;
                case "clan_net_worth": return LeaderboardMode.ClanNetWorth;
                case "clan_player_share": return LeaderboardMode.ClanPlayerShare;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        private static long NowMilliseconds() => Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

        private sealed class PendingRequest
        {
            private readonly Task<WireLeaderboardResponse>? _playerTask;
            private readonly Task<WireClanLeaderboardResponse>? _clanTask;
            internal readonly string requestId;
            internal readonly BridgeConnection connection;
            internal readonly LeaderboardQuery query;

            internal PendingRequest(string id, BridgeConnection client, LeaderboardQuery parsed,
                Task<WireLeaderboardResponse> task)
            { requestId = id; connection = client; query = parsed; _playerTask = task; }

            internal PendingRequest(string id, BridgeConnection client, LeaderboardQuery parsed,
                Task<WireClanLeaderboardResponse> task)
            { requestId = id; connection = client; query = parsed; _clanTask = task; }

            internal bool IsCompleted => _playerTask?.IsCompleted == true || _clanTask?.IsCompleted == true;

            internal LeaderboardSnapshotDto GetSnapshot()
            {
                if (_playerTask != null) return ConvertPlayer(query, _playerTask.GetAwaiter().GetResult());
                return ConvertClan(query, _clanTask!.GetAwaiter().GetResult());
            }
        }
    }
}
