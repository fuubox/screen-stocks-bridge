using ScreenStocksBridge;
using Xunit;

namespace ScreenStocksBridge.Tests;

public sealed class TradeRateLimiterTests
{
    [Fact]
    public void TradeRateLimiterAllowsBurstThenReportsRetryTime()
    {
        var limiter = new TradeRateLimiter();

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryConsume(1, 0, out _));

        Assert.False(limiter.TryConsume(1, 0, out var retryAfterMs));
        Assert.Equal(1000, retryAfterMs);
        Assert.False(limiter.TryConsume(1, 0.25, out retryAfterMs));
        Assert.Equal(750, retryAfterMs);
        Assert.True(limiter.TryConsume(1, 1, out retryAfterMs));
        Assert.Equal(0, retryAfterMs);
    }

    [Fact]
    public void TradeRateLimiterCapsRefillAtTenTokens()
    {
        var limiter = new TradeRateLimiter();

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryConsume(1, 0, out _));

        Assert.True(limiter.TryConsume(10, 100, out _));
        Assert.False(limiter.TryConsume(1, 100, out var retryAfterMs));
        Assert.Equal(1000, retryAfterMs);
    }

    [Theory]
    [InlineData("buy_max", false, false, 1)]
    [InlineData("buy_percent", false, true, 2)]
    [InlineData("short_max", false, true, 1)]
    [InlineData("short_percent", true, false, 2)]
    [InlineData("sell_max", true, true, 1)]
    [InlineData("sell_percent", true, false, 1)]
    [InlineData("cover_max", false, true, 1)]
    [InlineData("cover_percent", true, true, 1)]
    [InlineData("close_max", true, false, 1)]
    [InlineData("close_percent", true, true, 2)]
    public void TradeRatePolicyMatchesGameFileCommandCosts(
        string action, bool hasLong, bool hasShort, int expectedCost)
    {
        Assert.True(TradeRatePolicy.TryGetTokenCost(action, hasLong, hasShort, out var cost));
        Assert.Equal(expectedCost, cost);
    }

    [Fact]
    public void TradeRatePolicyRejectsUnknownActions()
    {
        Assert.False(TradeRatePolicy.TryGetTokenCost("ipo", false, false, out _));
    }

    [Fact]
    public void TradeRatePolicyRejectsClosingWhenNoPositionExists()
    {
        Assert.False(TradeRatePolicy.TryGetTokenCost("close_max", false, false, out _));
    }
}
