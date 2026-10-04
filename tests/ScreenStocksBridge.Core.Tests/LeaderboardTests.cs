using System.Text.Json;
using ScreenStocksBridge;
using Xunit;

namespace ScreenStocksBridge.Tests;

public sealed class LeaderboardTests
{
    [Theory]
    [InlineData("current", "current", 10, 0, "current:10")]
    [InlineData("ALL_TIME", "all_time", 10, 0, "all_time:10")]
    [InlineData("ipo", "ipo", 10, 0, "ipo:10")]
    [InlineData("current_top", "current_top", 0, 100, "current_top:100")]
    [InlineData("clan_net_worth", "clan_net_worth", 0, 0, "clan_net_worth")]
    [InlineData("clan_player_share", "clan_player_share", 0, 0, "clan_player_share")]
    public void LeaderboardQueryNormalizesModesAndBuildsDistinctCacheKeys(
        string inputMode, string expectedMode, int expectedRadius, int expectedCount, string expectedKey)
    {
        Assert.True(LeaderboardQuery.TryParse(new RequestParams { mode = inputMode },
            out var query, out var errorCode, out _), errorCode);

        Assert.Equal(expectedMode, query!.mode);
        Assert.Equal(expectedRadius, query.radius);
        Assert.Equal(expectedCount, query.count);
        Assert.Equal(expectedKey, query.cacheKey);
    }

    [Fact]
    public void LeaderboardQueryRejectsUnknownModesAndOutOfRangeRequests()
    {
        Assert.False(LeaderboardQuery.TryParse(new RequestParams { mode = "earnings" }, out _, out var badMode, out _));
        Assert.Equal("invalid_mode", badMode);
        Assert.False(LeaderboardQuery.TryParse(new RequestParams { mode = "current", radius = 101 }, out _, out var badRadius, out _));
        Assert.Equal("invalid_radius", badRadius);
        Assert.False(LeaderboardQuery.TryParse(new RequestParams { mode = "current_top", count = 101 }, out _, out var badCount, out _));
        Assert.Equal("invalid_count", badCount);
    }

    [Fact]
    public void PlayerLeaderboardPreservesExactValuesAndEscapesNames()
    {
        var snapshot = new LeaderboardSnapshotDto
        {
            mode = "all_time",
            fetchedAtUnixSeconds = 1791130000,
            totalRanked = 12000,
            selfRank = 42,
            players =
            {
                new LeaderboardPlayerEntryDto
                {
                    rank = 1,
                    steamId = "76561198000000000",
                    displayName = "Trader \"Ace\"",
                    clan = "Market\\Makers",
                    netWorth = "987654321012345678.125",
                    ipoCount = 7
                }
            }
        };

        using var document = JsonDocument.Parse(BridgeJson.SerializeLeaderboardSnapshot(snapshot, cached: false));
        var root = document.RootElement;
        var entry = root.GetProperty("entries")[0];

        Assert.Equal("all_time", root.GetProperty("mode").GetString());
        Assert.False(root.GetProperty("cached").GetBoolean());
        Assert.Equal(12000, root.GetProperty("totalRanked").GetInt64());
        Assert.Equal(42, root.GetProperty("selfRank").GetInt32());
        Assert.Equal("Trader \"Ace\"", entry.GetProperty("displayName").GetString());
        Assert.Equal("Market\\Makers", entry.GetProperty("clan").GetString());
        Assert.Equal("987654321012345678.125", entry.GetProperty("netWorth").GetString());
    }

    [Fact]
    public void ClanLeaderboardSerializesClanSpecificFields()
    {
        var snapshot = new LeaderboardSnapshotDto
        {
            mode = "clan_player_share",
            isClan = true,
            clans =
            {
                new LeaderboardClanEntryDto
                {
                    rank = 3,
                    clan = "Long-Term Holders",
                    netWorth = "123456789.5",
                    playerPercentage = 0.625
                }
            }
        };

        using var document = JsonDocument.Parse(BridgeJson.SerializeLeaderboardSnapshot(snapshot, cached: true));
        var root = document.RootElement;
        var entry = root.GetProperty("entries")[0];

        Assert.True(root.GetProperty("cached").GetBoolean());
        Assert.Equal("clan_player_share", root.GetProperty("mode").GetString());
        Assert.Equal(3, entry.GetProperty("rank").GetInt32());
        Assert.Equal("Long-Term Holders", entry.GetProperty("clan").GetString());
        Assert.Equal("123456789.5", entry.GetProperty("netWorth").GetString());
        Assert.Equal(0.625, entry.GetProperty("playerPercentage").GetDouble());
        Assert.False(entry.TryGetProperty("steamId", out _));
    }

    [Fact]
    public void LeaderboardCacheAllowsOnlyOneRequestAndEnforcesGlobalCooldown()
    {
        var cache = new LeaderboardRequestCache<string>();

        Assert.Equal(LeaderboardRequestStart.Started, cache.TryBeginFetch(0, out _));
        Assert.Equal(LeaderboardRequestStart.InFlight, cache.TryBeginFetch(1000, out var busyRetryMs));
        Assert.Equal(1000, busyRetryMs);

        cache.CompleteFetch("current:10", "snapshot", 2000);

        Assert.Equal(LeaderboardRequestStart.RateLimited, cache.TryBeginFetch(29999, out var cooldownMs));
        Assert.Equal(1, cooldownMs);
        Assert.Equal(LeaderboardRequestStart.Started, cache.TryBeginFetch(30000, out _));
    }

    [Fact]
    public void LeaderboardCacheIsKeyedByQueryAndExpiresAfterThirtySeconds()
    {
        var cache = new LeaderboardRequestCache<string>();
        Assert.Equal(LeaderboardRequestStart.Started, cache.TryBeginFetch(0, out _));
        cache.CompleteFetch("current:10", "current result", 1000);

        Assert.True(cache.TryGetCached("current:10", 2000, out var current));
        Assert.Equal("current result", current);
        Assert.False(cache.TryGetCached("ipo:10", 2000, out _));
        Assert.False(cache.TryGetCached("current:10", 31000, out _));
    }
}
