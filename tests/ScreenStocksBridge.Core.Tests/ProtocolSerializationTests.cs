using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ScreenStocksBridge;

public sealed class ProtocolSerializationTests
{
    [Fact]
    public void OfflineSummarySerializerMatchesSharedFixture()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "offline_summary_result.json");
        var expected = JsonNode.Parse(File.ReadAllText(fixturePath));
        var actual = BridgeJson.SerializeOfflineProgressSnapshot(new OfflineProgressSnapshotDto
        {
            available = true,
            summary = new OfflineProgressSummaryDto
            {
                total = "1024.5",
                generators = "120",
                dividends = "30.5",
                autoActions = "874",
                secondsAway = 3600,
                cappedEarningsSeconds = 7200,
                showEarnings = true,
                positionChanges =
                {
                    new OfflinePositionChangeDto
                    {
                        stockId = "$TECH",
                        isLong = true,
                        cashChange = "-500.25",
                        percentChange = -10.5f
                    }
                }
            }
        });

        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual)), actual);
    }

    [Fact]
    public void UnavailableOfflineSummaryHasNullSummary()
    {
        using var document = JsonDocument.Parse(BridgeJson.SerializeOfflineProgressSnapshot(new OfflineProgressSnapshotDto()));

        Assert.False(document.RootElement.GetProperty("available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("summary").ValueKind);
    }

    [Fact]
    public void ResponseEscapesIdAndPreservesRawResultJson()
    {
        const string id = "quote \" slash \\ newline\n";
        using var document = JsonDocument.Parse(ProtocolJson.Response(id, true, "{\"value\":7}", string.Empty));

        Assert.Equal(id, document.RootElement.GetProperty("id").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(7, document.RootElement.GetProperty("result").GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
    }

    [Fact]
    public void ErrorEscapesMessageAndClampsNegativeRetryDelay()
    {
        using var document = JsonDocument.Parse(ProtocolJson.Error("id", "bad", "line\nquote \"", -5));
        var error = document.RootElement.GetProperty("error");

        Assert.Equal("line\nquote \"", error.GetProperty("message").GetString());
        Assert.Equal(0, error.GetProperty("retryAfterMs").GetInt32());
    }

    [Fact]
    public void TopLevelObjectExtractionIgnoresBracesInsideEscapedStrings()
    {
        const string json = "{\"token\":\"t\",\"params\":{\"stockId\":\"TECH\",\"text\":\"{ inner } and \\\"quote\\\"\",\"nested\":[{\"n\":1}]},\"id\":\"a\"}";
        var parameters = JsonObjectParser.ExtractTopLevelObject(json, "params");

        Assert.NotNull(parameters);
        using var document = JsonDocument.Parse(parameters);
        Assert.Equal("TECH", document.RootElement.GetProperty("stockId").GetString());
        Assert.Equal("{ inner } and \"quote\"", document.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("nested")[0].GetProperty("n").GetInt32());
    }

    [Theory]
    [InlineData("$TECH", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("$" + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", false)]
    public void StockIdValidationEnforcesNonEmpty128CharacterLimit(string stockId, bool expected)
    {
        Assert.Equal(expected, RequestValidation.IsValidStockId(stockId));
    }

    [Fact]
    public void StockIdValidationAcceptsExactly128Characters()
    {
        Assert.True(RequestValidation.IsValidStockId("$" + new string('x', 127)));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.01f, true)]
    [InlineData(100f, true)]
    [InlineData(100.01f, false)]
    [InlineData(float.NaN, false)]
    [InlineData(float.PositiveInfinity, false)]
    public void TradePercentValidationRejectsInvalidAndNonFiniteValues(float percent, bool expected)
    {
        Assert.Equal(expected, RequestValidation.IsValidTradePercent(percent));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void UpgradeQuantityValidationEnforcesGameRequestBounds(int quantity, bool expected)
    {
        Assert.Equal(expected, RequestValidation.IsValidUpgradeQuantity(quantity));
    }
    [Fact]
    public void AutoActionToastSerializerPreservesDisplayedTextAndActionContext()
    {
        const string expected = "{\"text\":\"Executed Buy for $TECH\",\"stockId\":\"$TECH\",\"actionType\":\"Buy\",\"condition\":\"Above\",\"targetPrice\":125.5}";
        var actual = BridgeJson.SerializeAutoActionToast(new AutoActionToastDto
        {
            text = "Executed Buy for $TECH",
            stockId = "$TECH",
            actionType = "Buy",
            condition = "Above",
            targetPrice = 125.5f
        });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SnapshotPreservesEffectiveMaxVolumeSeparatelyAndExactly()
    {
        var stock = new StockDto
        {
            stockId = "$TECH",
            maxVolume = 30000,
            availableShares = 120
        };
        var effectiveVolumeField = typeof(StockDto).GetField("effectiveMaxVolume");
        Assert.NotNull(effectiveVolumeField);
        effectiveVolumeField!.SetValue(stock, "12345678901234567890");

        var snapshot = new StateSnapshotDto();
        snapshot.stocks.Add(stock);
        using var document = JsonDocument.Parse(BridgeJson.SerializeSnapshot(snapshot));
        var serializedStock = document.RootElement.GetProperty("stocks")[0];

        Assert.Equal(30000, serializedStock.GetProperty("maxVolume").GetInt32());
        Assert.Equal("12345678901234567890", serializedStock.GetProperty("effectiveMaxVolume").GetString());
        Assert.Equal(120, serializedStock.GetProperty("availableShares").GetInt32());
    }
}
