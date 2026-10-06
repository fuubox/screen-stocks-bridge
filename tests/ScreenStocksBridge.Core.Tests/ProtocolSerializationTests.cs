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
    public void NewsTickerSerializerPreservesMarketHeadlineTextAndSourceFields()
    {
        var actual = BridgeJson.SerializeNewsTickerItem(new NewsTickerItemDto
        {
            type = "market",
            text = "<color=#fff>$TECH rose</color>",
            id = "news-id",
            cursor = "news-cursor",
            createdAtMs = 1700000000123,
            stockId = "$TECH",
            price = 12.5f,
            kind = "price-change",
            lookbackMinutes = 15,
            debug = true
        });
        using var document = JsonDocument.Parse(actual);
        var item = document.RootElement;

        Assert.Equal("market", item.GetProperty("type").GetString());
        Assert.Equal("<color=#fff>$TECH rose</color>", item.GetProperty("text").GetString());
        Assert.Equal("news-id", item.GetProperty("id").GetString());
        Assert.Equal("news-cursor", item.GetProperty("cursor").GetString());
        Assert.Equal(1700000000123, item.GetProperty("createdAtMs").GetInt64());
        Assert.Equal("$TECH", item.GetProperty("stockId").GetString());
        Assert.Equal(12.5, item.GetProperty("price").GetDouble());
        Assert.Equal("price-change", item.GetProperty("kind").GetString());
        Assert.Equal(15, item.GetProperty("lookbackMinutes").GetDouble());
        Assert.True(item.GetProperty("debug").GetBoolean());
        Assert.False(item.TryGetProperty("targetPrice", out _));
    }

    [Fact]
    public void NewsTickerSerializerPreservesScheduledPriceHeadlineFields()
    {
        var actual = BridgeJson.SerializeNewsTickerItem(new NewsTickerItemDto
        {
            type = "scheduled_price",
            text = "Scheduled price update",
            id = "announcement-id",
            cursor = "announcement-cursor",
            occurrenceId = "occurrence-id",
            revision = "revision-2",
            stockId = "$TECH",
            targetPrice = 14.25f,
            scheduledAtMs = 1700000001000,
            reminderOffsetMs = 300000,
            publishedAtMs = 1699999700000,
            direction = "up"
        });
        using var document = JsonDocument.Parse(actual);
        var item = document.RootElement;

        Assert.Equal("scheduled_price", item.GetProperty("type").GetString());
        Assert.Equal("Scheduled price update", item.GetProperty("text").GetString());
        Assert.Equal("announcement-id", item.GetProperty("id").GetString());
        Assert.Equal("announcement-cursor", item.GetProperty("cursor").GetString());
        Assert.Equal("occurrence-id", item.GetProperty("occurrenceId").GetString());
        Assert.Equal("revision-2", item.GetProperty("revision").GetString());
        Assert.Equal("$TECH", item.GetProperty("stockId").GetString());
        Assert.Equal(14.25, item.GetProperty("targetPrice").GetDouble());
        Assert.Equal(1700000001000, item.GetProperty("scheduledAtMs").GetInt64());
        Assert.Equal(300000, item.GetProperty("reminderOffsetMs").GetInt64());
        Assert.Equal(1699999700000, item.GetProperty("publishedAtMs").GetInt64());
        Assert.Equal("up", item.GetProperty("direction").GetString());
        Assert.False(item.TryGetProperty("price", out _));
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
