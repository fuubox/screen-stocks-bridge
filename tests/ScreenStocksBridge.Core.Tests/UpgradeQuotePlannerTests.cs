using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ScreenStocksBridge;

public sealed class UpgradeQuotePlannerTests
{
    private static UpgradeQuotePlan<decimal> Quote(int level = 0, int cap = int.MaxValue,
        int limit = 5000, int? quantity = null, decimal? budget = null,
        Func<int, bool>? target = null, Func<int, decimal>? price = null) =>
        UpgradeQuotePlanner.Plan(level, cap, limit, quantity, target, budget.HasValue,
            budget ?? 0m, 0m, price ?? (n => 10m * (n + 1)), (a, b) => a + b, decimal.Compare);

    [Fact]
    public void BudgetIncludesExactFitButNotNextLevel()
    {
        var result = Quote(budget: 60m);
        Assert.Equal(3, result.Quantity);
        Assert.Equal(60m, result.TotalCost);
        Assert.Equal("budget_exhausted", result.StopReason);
        Assert.Equal(0, Quote(budget: 9m).Quantity);
    }

    [Fact]
    public void FixedQuantityCanQuoteBeyondCashWithoutBudgetConstraint()
    {
        var result = Quote(level: 2, quantity: 3);
        Assert.Equal(120m, result.TotalCost);
        Assert.Equal(5, result.LevelAfter);
        Assert.Equal("quantity_reached", result.StopReason);
    }

    [Fact]
    public void CapAndLimitAreDistinctAndAlreadyMaxedCostsNothing()
    {
        Assert.Equal("maxed", Quote(cap: 2, quantity: 10).StopReason);
        Assert.Equal(0m, Quote(level: 2, cap: 2).TotalCost);
        var limited = Quote(limit: 2, budget: 1000m);
        Assert.Equal(2, limited.Quantity);
        Assert.Equal("calculation_limit", limited.StopReason);
        Assert.Equal("quantity_reached", Quote(limit: 2, quantity: 2).StopReason);
    }

    [Fact]
    public void TargetsStopAtFirstMeetingIncludingAlreadyMetAndDecreasingValues()
    {
        Assert.Equal(0, Quote(target: n => n >= 0).Quantity);
        Assert.Equal(4, Quote(target: n => 10 - 2 * n <= 3).Quantity);
        Assert.Equal("maxed", Quote(cap: 2, target: n => n >= 10).StopReason);
    }

    [Fact]
    public void FreeLevelsCountAndNegativePricesFailRatherThanUnderquote()
    {
        var result = Quote(quantity: 3, budget: 0m, price: _ => 0m);
        Assert.Equal(3, result.Quantity);
        Assert.Throws<InvalidOperationException>(() => Quote(quantity: 1, price: _ => -1m));
    }

    [Fact]
    public void LargeMoneyAndFinalIntegerLevelDoNotLosePrecisionOrOverflow()
    {
        const decimal cost = 12345678901234567890123.123456m;
        Assert.Equal(cost * 2m, Quote(quantity: 2, price: _ => cost).TotalCost);
        var last = Quote(level: int.MaxValue - 1, quantity: 2, price: _ => 1m);
        Assert.Equal(int.MaxValue, last.LevelAfter);
        Assert.Equal(1, last.Quantity);
        Assert.Equal("maxed", last.StopReason);
    }

    [Fact]
    public void QuoteSerializationKeepsMoneyAsStringsAndOptionalFieldsNull()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            using var json = JsonDocument.Parse(BridgeJson.SerializeUpgradeQuote(new UpgradeQuoteDto
            {
                upgradeId = "DividendGain", totalCost = "12345678901234567890.123456",
                resultingValue = 1.25, truncated = true, stopReason = "calculation_limit",
            }));
            var root = json.RootElement;
            Assert.Equal("12345678901234567890.123456", root.GetProperty("totalCost").GetString());
            Assert.Equal(1.25, root.GetProperty("resultingValue").GetDouble());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("budget").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("targetReached").ValueKind);
            Assert.True(root.GetProperty("truncated").GetBoolean());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
