using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace ScreenStocksBridge;

public sealed class MarketStreamTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void RawTypeClassificationIgnoresNestedTypesAndHandlesEscapedPropertyStrings()
    {
        var raw = "{\"data\":{\"type\":\"state\"},\"ty\\u0070e\":\"price\\u0056ector\"}";
        Assert.Equal("priceVector", JsonObjectParser.ExtractTopLevelString(raw, "type"));
        Assert.Null(JsonObjectParser.ExtractTopLevelString("{\"data\":{\"type\":\"prices\"}}", "type"));
        Assert.Null(JsonObjectParser.ExtractTopLevelString("{ ", "type"));
    }

    [Fact]
    public void ClockReportsMillisecondsAndWithholdsUnsynchronizedSchedule()
    {
        var clock = Parse(MarketClock.Json(true, 120500, 905, 100, 100000, 10000));
        Assert.Equal(130000, clock["nextDividendBoundaryMs"]!.GetValue<long>());
        Assert.Equal(9500, clock["remainingMs"]!.GetValue<long>());
        Assert.Equal(0.05, clock["cycleProgress"]!.GetValue<double>(), 8);
        var unknown = Parse(MarketClock.Json(false, 120500, 905, 100, 100000, 10000));
        Assert.Null(unknown["serverTimeMs"]);
        Assert.Null(unknown["nextDividendBoundaryMs"]);
        Assert.Null(unknown["remainingMs"]);
        Assert.Null(unknown["cycleProgress"]);
        Assert.Equal("local_game_settings", unknown["scheduleProvenance"]!.GetValue<string>());
    }

    [Fact]
    public void ClockAtBoundaryMeansTheNextCycleAndInvalidIntervalsHaveNoTimer()
    {
        Assert.Equal(130000, Parse(MarketClock.Json(true, 120000, 0, 100, 100000, 10000))["nextDividendBoundaryMs"]!.GetValue<long>());
        Assert.Null(Parse(MarketClock.Json(true, 120000, 0, 100, 100000, 0))["nextDividendBoundaryMs"]);
        Assert.Null(Parse(MarketClock.Json(true, long.MaxValue, 0, 100, 0, 10000))["nextDividendBoundaryMs"]);
    }

    [Fact]
    public void MaterialComparisonIgnoresTimeButRetainsEconomicChangesAndRestoresResponse()
    {
        var snapshot = new StateSnapshotDto { ready = true, serverTick = 1, marketClock = "{\"serverTimeMs\":1}" };
        snapshot.cooldowns.serverNowUnixSeconds = 1;
        snapshot.cooldowns.buy.availableAtUnixSeconds = 10;
        snapshot.cooldowns.buy.remainingSeconds = 9; snapshot.cooldowns.buy.active = true;
        snapshot.autoActions.actions.Add(new AutoActionDto { cooldownUntilUnixSeconds = 12, cooldownRemainingSeconds = 11, onCooldown = true });
        var first = MarketClock.MaterialSnapshot(snapshot);
        Assert.Equal(9, snapshot.cooldowns.buy.remainingSeconds);
        snapshot.serverTick = 2; snapshot.marketClock = "{\"serverTimeMs\":2}";
        snapshot.cooldowns.serverNowUnixSeconds = 2; snapshot.cooldowns.buy.remainingSeconds = 8;
        snapshot.autoActions.actions[0].cooldownRemainingSeconds = 10;
        Assert.Equal(first, MarketClock.MaterialSnapshot(snapshot));
        Assert.Equal(10, snapshot.autoActions.actions[0].cooldownRemainingSeconds);
        snapshot.cooldowns.buy.availableAtUnixSeconds = 11;
        Assert.NotEqual(first, MarketClock.MaterialSnapshot(snapshot));
        Assert.Contains("\"serverTimeMs\":2", BridgeJson.SerializeSnapshot(snapshot));
        snapshot.cash = "99";
        Assert.Contains("99", MarketClock.MaterialSnapshot(snapshot));
    }

    [Fact]
    public void CadenceDefaultsToFourHertzAndAcceptsAnyFinitePositiveRate()
    {
        var cadence = new MarketCadence();
        Assert.Equal(4, cadence.RateHz);
        Assert.True(cadence.Due(0)); Assert.False(cadence.Due(.249)); Assert.True(cadence.Due(.25));
        cadence.SetRate(10000);
        Assert.True(cadence.Due(.26)); Assert.False(cadence.Due(.26001));
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => cadence.SetRate(invalid));
    }

    [Fact]
    public void CandlesUseSourceUtcSecondsAndCountUnchangedObservationsWithoutSyntheticBars()
    {
        var stream = new CandleStream { Paused = false };
        var output = new List<CandleUpdate>(); stream.Updated += output.Add;
        stream.Add("A", 10, 1100, 1); stream.Add("A", 10, 1200, 2);
        stream.Add("A", 15, 1900, 3); stream.Add("A", 8, 1800, 4);
        Assert.Empty(output);
        stream.Advance(2000);
        var bar = Parse(Assert.Single(output).Json);
        Assert.Equal(1000, bar["bucketStartMs"]!.GetValue<long>());
        Assert.Equal(10, bar["open"]!.GetValue<double>()); Assert.Equal(15, bar["close"]!.GetValue<double>());
        Assert.Equal(15, bar["high"]!.GetValue<double>()); Assert.Equal(8, bar["low"]!.GetValue<double>());
        Assert.Equal(4, bar["quoteCount"]!.GetValue<long>());
        stream.Add("A", 30, 5100, 5); stream.Advance(6000);
        Assert.Equal(new long[] { 1000, 5000 }, output.Select(x => x.BucketStartMs));
    }

    [Fact]
    public void EqualSourceTimesUseTickThenAcceptedArrivalForOpenAndClose()
    {
        var stream = new CandleStream { Paused = false };
        CandleUpdate? output = null; stream.Updated += value => output = value;
        stream.Add("A", 10, 1100, 10); stream.Add("A", 11, 1100, 9); stream.Add("A", 12, 1100, 10);
        stream.Advance(2000);
        var bar = Parse(output!.Json);
        Assert.Equal(11, bar["open"]!.GetValue<double>());
        Assert.Equal(12, bar["close"]!.GetValue<double>());
        Assert.Equal(9, bar["firstTick"]!.GetValue<long>());
    }

    [Fact]
    public void ConnectedCandlesCarryOnlyConsecutiveSameStockClosesWithoutAddingObservations()
    {
        var stream = new CandleStream(connected: true) { Paused = false };
        var output = new List<CandleUpdate>(); stream.Updated += output.Add;
        stream.Add("WAVE", 10, 1100, 1);
        stream.Add("OTHER", 99, 2100, 2);
        stream.Add("WAVE", 8, 2100, 2); stream.Advance(3000);
        var bar = Parse(output.Last(x => x.BucketStartMs == 2000 && Parse(x.Json)["stockId"]!.GetValue<string>() == "WAVE").Json);
        Assert.Equal(10, bar["open"]!.GetValue<double>());
        Assert.Equal(10, bar["high"]!.GetValue<double>());
        Assert.Equal(8, bar["low"]!.GetValue<double>());
        Assert.Equal(8, bar["close"]!.GetValue<double>());
        Assert.Equal(1, bar["quoteCount"]!.GetValue<long>());
        Assert.Equal(2100, bar["firstSourceTimeMs"]!.GetValue<long>());
        Assert.Equal("connected", bar["candleMode"]!.GetValue<string>());
        Assert.Equal("previous_close", bar["openProvenance"]!.GetValue<string>());
        stream.Add("WAVE", 20, 4100, 4); stream.Advance(5000);
        bar = Parse(output.Last().Json);
        Assert.Equal(20, bar["open"]!.GetValue<double>());
        Assert.Equal("source_observation", bar["openProvenance"]!.GetValue<string>());
        Assert.DoesNotContain(output, x => x.BucketStartMs == 3000);
        var generation = stream.Generation; stream.Reset(); stream.Paused = false;
        stream.Add("WAVE", 30, 5100, 5); stream.Advance(6000);
        Assert.NotEqual(generation, stream.Generation);
        Assert.Equal(30, Parse(output.Last().Json)["open"]!.GetValue<double>());
    }

    [Fact]
    public void ConnectedLatePredecessorRevisesSuccessorAndCanShrinkItsBounds()
    {
        var stream = new CandleStream(true) { Paused = false };
        stream.Add("WAVE", 50, 1100, 1); stream.Add("WAVE", 10, 2100, 2); stream.Advance(3000);
        stream.Add("WAVE", 8, 1900, 3);
        var successor = Parse(stream.Replay("", 0).Updates.Last().Json);
        Assert.Equal(2000, successor["bucketStartMs"]!.GetValue<long>());
        Assert.Equal(2, successor["revision"]!.GetValue<long>());
        Assert.Equal(8, successor["open"]!.GetValue<double>());
        Assert.Equal(10, successor["high"]!.GetValue<double>());
        Assert.Equal(8, successor["low"]!.GetValue<double>());
        Assert.Equal(1, successor["quoteCount"]!.GetValue<long>());
        // An earlier quote changes the predecessor's observed open, not its close.
        var sequence = stream.Sequence;
        stream.Add("WAVE", 100, 1050, 0);
        Assert.Equal(sequence + 1, stream.Sequence);
    }

    [Fact]
    public void ConnectedLateInsertionConnectsAlreadyPublishedSuccessorAndKeepsReplayIndependent()
    {
        var stream = new CandleStream(true) { Paused = false };
        var observed = new CandleStream { Paused = false };
        stream.Add("WAVE", 10, 2100, 2); stream.Advance(3000);
        stream.Add("WAVE", 20, 1500, 1);
        var bar = Parse(stream.Replay("", 0).Updates.Last().Json);
        Assert.Equal(2000, bar["bucketStartMs"]!.GetValue<long>());
        Assert.Equal(20, bar["open"]!.GetValue<double>());
        Assert.True(observed.Replay(stream.Generation, stream.Sequence).Gap);
        Assert.Single(stream.Replay(stream.Generation, stream.Sequence - 1).Updates);
    }

    [Fact]
    public void ConnectedCorrectionAtRetentionEdgeStillUsesItsOriginalPredecessor()
    {
        var stream = new CandleStream(true) { Paused = false };
        stream.Add("WAVE", 5, 1900, 1); stream.Add("WAVE", 10, 2700, 2);
        stream.Advance(17500); stream.Add("WAVE", 12, 2800, 3);
        var bar = Parse(stream.Replay("", 0).Updates.Last().Json);
        Assert.Equal(5, bar["open"]!.GetValue<double>());
        Assert.Equal(5, bar["low"]!.GetValue<double>());
        Assert.Equal(2, bar["quoteCount"]!.GetValue<long>());
        Assert.All(stream.Replay("", 0).Updates, x => Assert.True(x.BucketStartMs >= 2000));
    }

    [Fact]
    public void LateQuotesReviseClosedCandlesAndReplayEmittedRevisionsInOrder()
    {
        var stream = new CandleStream { Paused = false };
        stream.Add("A", 10, 1500, 10); stream.Advance(2000);
        stream.Add("A", 5, 1100, 9);
        var replay = stream.Replay(stream.Generation, 0);
        Assert.False(replay.Gap); Assert.Equal(2, replay.Updates.Count);
        var revised = Parse(replay.Updates[1].Json);
        Assert.Equal(2, revised["revision"]!.GetValue<long>());
        Assert.Equal(5, revised["open"]!.GetValue<double>());
        Assert.Equal(2, revised["quoteCount"]!.GetValue<long>());
        Assert.Single(stream.Replay(stream.Generation, 1).Updates);
    }

    [Fact]
    public void RetentionRejectsOldQuotesAndReportsExpiredOrForeignCursors()
    {
        var stream = new CandleStream { Paused = false };
        stream.Add("A", 10, 1100, 1); stream.Advance(2000);
        stream.Add("A", 20, 17100, 2); stream.Advance(18000);
        stream.Add("A", 99, 1500, 3);
        Assert.Equal(1, stream.LateObservations);
        var replay = stream.Replay(stream.Generation, 0);
        Assert.True(replay.Gap); Assert.Single(replay.Updates);
        Assert.True(stream.Replay("old-session", stream.Sequence).Gap);
        Assert.True(stream.Replay(stream.Generation, stream.Sequence + 1).Gap);
        var generation = stream.Generation; stream.Reset(); Assert.NotEqual(generation, stream.Generation);
    }

    [Fact]
    public void ExpiredLateRevisionsInsideReplayLogProduceAnExplicitGap()
    {
        var stream = new CandleStream { Paused = false };
        stream.Add("A", 1, 1100, 1); stream.Advance(2000);
        stream.Add("A", 2, 4100, 2); stream.Advance(5000);
        stream.Add("A", 3, 1500, 3); // sequence 3 is for an older bucket than sequence 2
        stream.Advance(18000);
        var replay = stream.Replay(stream.Generation, 1);
        Assert.True(replay.Gap);
        Assert.All(replay.Updates, x => Assert.True(x.BucketStartMs >= 3000));
    }

    [Fact]
    public void QuoteWithinFifteenSecondsCanCorrectAPartiallyOverlappingBoundarySecond()
    {
        var stream = new CandleStream { Paused = false };
        stream.Advance(17500);
        stream.Add("A", 4, 2700, 1);
        Assert.Equal(0, stream.LateObservations);
        var bar = Assert.Single(stream.Replay("", 0).Updates);
        Assert.Equal(2000, bar.BucketStartMs);
        stream.Add("A", 99, 2400, 0);
        Assert.Equal(1, stream.LateObservations);
    }

    [Fact]
    public void HttpPauseNeverAddsQuotesOrPublishesCandlesUntilWebsocketResumes()
    {
        var stream = new CandleStream { Paused = false };
        var output = new List<CandleUpdate>(); stream.Updated += output.Add;
        stream.Add("A", 10, 1100, 1); stream.Paused = true;
        stream.Advance(2000); stream.Add("A", 100, 1500, 2);
        Assert.Empty(output);
        stream.Paused = false; stream.Advance(2100);
        Assert.Equal(1, Parse(Assert.Single(output).Json)["quoteCount"]!.GetValue<long>());
    }

    [Fact]
    public void LaterSourceTimestampClosesWhenClockIsUnsynchronizedAndInvalidPricesAreIgnored()
    {
        var stream = new CandleStream { Paused = false };
        var output = new List<CandleUpdate>(); stream.Updated += output.Add;
        stream.Add("A", 1, 1100, 1);
        stream.Add("A", double.NaN, 1500, 2); stream.Add("A", -1, 1500, 2);
        stream.Add("A", 2, 2100, 3);
        Assert.Equal(1, Parse(Assert.Single(output).Json)["quoteCount"]!.GetValue<long>());
    }
}
