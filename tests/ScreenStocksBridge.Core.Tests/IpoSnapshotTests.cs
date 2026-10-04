using System.Text.Json;
using ScreenStocksBridge;
using Xunit;

namespace ScreenStocksBridge.Tests;

public sealed class IpoSnapshotTests
{
    [Fact]
    public void IpoSnapshotPreservesEligibilityAndLargeCurrencyValues()
    {
        var snapshot = new IpoSnapshotDto
        {
            ready = true,
            unlocked = true,
            eligible = false,
            ipoCount = 2,
            effectiveIpoCount = 3,
            roundPeakNetWorth = "9007199254740993.125",
            requiredNetWorth = "10000000000000000.5",
        };

        using var document = JsonDocument.Parse(BridgeJson.SerializeIpoSnapshot(snapshot));
        var root = document.RootElement;

        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.True(root.GetProperty("unlocked").GetBoolean());
        Assert.False(root.GetProperty("eligible").GetBoolean());
        Assert.Equal(2, root.GetProperty("ipoCount").GetInt32());
        Assert.Equal(3, root.GetProperty("effectiveIpoCount").GetInt32());
        Assert.Equal("9007199254740993.125", root.GetProperty("roundPeakNetWorth").GetString());
        Assert.Equal("10000000000000000.5", root.GetProperty("requiredNetWorth").GetString());
    }

    [Theory]
    [InlineData(false, false, false, "not_ready")]
    [InlineData(true, false, false, "ipo_locked")]
    [InlineData(true, true, false, "ipo_requirements_not_met")]
    public void IpoTriggerPolicyRejectsRequestsTheGameWouldNotAllow(
        bool ready, bool unlocked, bool eligible, string expectedError)
    {
        Assert.False(IpoTriggerPolicy.TryAuthorize(ready, unlocked, eligible, out var errorCode, out _));
        Assert.Equal(expectedError, errorCode);
    }

    [Fact]
    public void IpoTriggerPolicyAllowsOnlyReadyUnlockedEligibleRequests()
    {
        Assert.True(IpoTriggerPolicy.TryAuthorize(true, true, true, out var errorCode, out var errorMessage));
        Assert.Equal(string.Empty, errorCode);
        Assert.Equal(string.Empty, errorMessage);
    }
}
