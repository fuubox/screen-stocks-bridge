using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ScreenStocks.Net;
using ScreenStocksBridge;

internal static class MarketStreamRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run()
    {
        var manager = New(typeof(StockManager));
        MakeUnityAlive(manager);
        var source = New(typeof(RemoteMarketDataSource));
        var realtime = (ScreenStocksRealtimeClient)New(typeof(ScreenStocksRealtimeClient));
        foreach (var name in new[] { "<QueuePreview>k__BackingField", "_serverCapabilities", "_stateDeltaBaseline" })
        { var field = typeof(ScreenStocksRealtimeClient).GetField(name, Flags); field.SetValue(realtime, Activator.CreateInstance(field.FieldType, true)); }
        Set(source, "_realtime", realtime); Set(source, "<IsReady>k__BackingField", true);
        Set(manager, "_source", source); Set(realtime, "_ready", true);
        var stockSingleton = typeof(StockManager).GetField("<I>k__BackingField", Static);
        var savedStock = stockSingleton.GetValue(null); stockSingleton.SetValue(null, manager);
        var server = new BridgeServer(0, "offline-test", 4);
        var connection = Connection();
        ((List<BridgeConnection>)typeof(BridgeServer).GetField("_clients", Flags).GetValue(server)).Add(connection);
        var module = new MarketStreamModule(new StateService(), () => server);
        try
        {
            module.Install();
            module.Handle(new BridgeRequest { id = "capabilities", method = "market.stream_status" }, connection);
            Require(Frames(connection).Any(x => x.Contains("\"bridgeVersion\":\"0.17.0\"") && x.Contains("\"candleSchemaVersion\":1")),
                "canonical candle consumers can verify the minimum bridge version and candle schema");
            module.SubscribeState(connection);
            var baseline = "{ \"type\":\"priceVectorBaseline\",\"sequence\":1,\"data\":{\"layoutRevision\":\"L\",\"stockIds\":[\"A\"],\"prices\":[10],\"serverTimeMs\":1100,\"tick\":1}}";
            WithRaw(realtime, baseline, () => typeof(ScreenStocksRealtimeClient).GetMethod("ApplyPriceVectorBaseline", Flags).Invoke(realtime,
                new object[] { new RealtimePriceVectorData { layoutRevision = "L", stockIds = new List<string> { "A" },
                    prices = new List<float> { 10 }, serverTimeMs = 1100, tick = 1 } }));
            Require(!Frames(connection).Any(x => x.Contains("\"event\":\"market.price\"")), "instant mode defaults off");
            var prior = Frames(connection).Count;
            module.Handle(new BridgeRequest { id = "instant", method = "market.set_instant", @params = new RequestParams { enabled = true } }, connection);
            var enabled = Frames(connection).Skip(prior).ToList();
            var snapshotIndex = enabled.FindIndex(x => x.Contains("\"event\":\"market.updated\""));
            var baselineIndex = enabled.FindIndex(x => x.Contains("\"event\":\"market.price\""));
            Require(snapshotIndex >= 0 && baselineIndex > snapshotIndex, "fresh full snapshot precedes cached raw baseline");
            Require(enabled[baselineIndex].Contains("\"sourceJson\":" + BridgeJson.Quote(baseline)), "raw baseline whitespace and source envelope preserved exactly");
            Require(enabled[baselineIndex].Contains("\"replayedBaseline\":true"), "baseline replay is explicit");
            module.Handle(new BridgeRequest { id = "rate", method = "market.set_rate", @params = new RequestParams { rateHz = 1000 } }, connection);
            Require(Frames(connection).Any(x => x.Contains("\"snapshotRateHz\":1000,\"instant\":true")), "global cadence changes leave instant mode enabled");
            module.Handle(new BridgeRequest { id = "bad-rate", method = "market.set_rate", @params = new RequestParams { rateHz = 0 } }, connection);
            Require(Frames(connection).Any(x => x.Contains("invalid_params")), "invalid cadence is an explicit protocol error");

            var vector = "{\"type\":\"priceVector\",\"sequence\":2,\"data\":{\"layoutRevision\":\"L\",\"prices\":[12],\"serverTimeMs\":2100,\"tick\":2}}";
            WithRaw(realtime, vector, () => typeof(ScreenStocksRealtimeClient).GetMethod("ApplyPriceVector", Flags).Invoke(realtime,
                new object[] { new RealtimePriceVectorData { layoutRevision = "L", prices = new List<float> { 12 }, serverTimeMs = 2100, tick = 2 } }));
            Require(Frames(connection).Any(x => x.Contains("\"sourceJson\":" + BridgeJson.Quote(vector))), "native accepted vector reaches raw tunnel");
            var candles = (CandleStream)typeof(MarketStreamModule).GetField("_candles", Flags).GetValue(module);
            Require(candles.Sequence == 1 && candles.Replay("", 0).Updates[0].Json.Contains("\"quoteCount\":1"), "replaying baseline does not inflate observations");
            var priceCount = Frames(connection).Count(x => x.Contains("\"event\":\"market.price\""));
            module.Accept(realtime, "{\"type\":\"state\",\"secret\":\"not-price\"}", null, new[] { "A" }, new float[] { 99 }, 2200, 3);
            Require(Frames(connection).Count(x => x.Contains("\"event\":\"market.price\"")) == priceCount, "non-price messages never tunneled");

            var second = Connection();
            ((List<BridgeConnection>)typeof(BridgeServer).GetField("_clients", Flags).GetValue(server)).Add(second);
            module.SubscribeState(second);
            Require(Frames(second).Any(x => x.Contains("\"replayedBaseline\":true")), "future subscribers bootstrap while instant mode is active");
            module.Handle(new BridgeRequest { id = "candles", method = "market.candles.subscribe", @params = new RequestParams() }, second);
            module.Update();
            Require(Frames(second).Any(x => x.Contains("\"event\":\"market.candle\"")), "separate candle subscription replays retained source bars");
            var sourceStatus = Frames(connection).Last(x => x.Contains("\"event\":\"market.feed_status\""));
            var rawGeneration = JsonObjectParser.ExtractTopLevelString(JsonObjectParser.ExtractTopLevelObject(sourceStatus, "data")!, "sourceGeneration");
            var candleGeneration = candles.Generation;
            typeof(ScreenStocksRealtimeClient).GetMethod("SetReady", Flags).Invoke(realtime, new object[] { false });
            Require(candles.Paused, "native HTTP fallback transition pauses canonical candles");
            sourceStatus = Frames(connection).Last(x => x.Contains("\"event\":\"market.feed_status\""));
            Require(rawGeneration != JsonObjectParser.ExtractTopLevelString(JsonObjectParser.ExtractTopLevelObject(sourceStatus, "data")!, "sourceGeneration"), "raw transport generation resets before source sequences can restart");
            Require(candles.Generation == candleGeneration, "transport reconnect preserves candle cursor recovery window");
            Require(Frames(connection).Any(x => x.Contains("http_fallback")), "HTTP fallback status is observable");
            var fallbackSubscriber = Connection();
            ((List<BridgeConnection>)typeof(BridgeServer).GetField("_clients", Flags).GetValue(server)).Add(fallbackSubscriber);
            module.Handle(new BridgeRequest { id = "paused-replay", method = "market.candles.subscribe", @params = new RequestParams() }, fallbackSubscriber);
            module.Update();
            Require(!Frames(fallbackSubscriber).Any(x => x.Contains("\"event\":\"market.candle\"")), "candle replay publication pauses during HTTP fallback");
            Set(realtime, "_ready", true);
            var resumedBaseline = "{\"type\":\"priceVectorBaseline\",\"sequence\":3,\"data\":{\"layoutRevision\":\"L\",\"stockIds\":[\"A\"],\"prices\":[13],\"serverTimeMs\":3100,\"tick\":3}}";
            WithRaw(realtime, resumedBaseline, () => typeof(ScreenStocksRealtimeClient).GetMethod("ApplyPriceVectorBaseline", Flags).Invoke(realtime,
                new object[] { new RealtimePriceVectorData { layoutRevision = "L", stockIds = new List<string> { "A" },
                    prices = new List<float> { 13 }, serverTimeMs = 3100, tick = 3 } }));
            module.Update();
            Require(Frames(fallbackSubscriber).Any(x => x.Contains("\"event\":\"market.candle\"")), "pending replay resumes after accepted websocket prices");
            VerifyConnectedSubscriptions(module, realtime, server);
            VerifyPayoutConfirmation(module, realtime, connection);
            var expiredSubscriber = Connection();
            ((List<BridgeConnection>)typeof(BridgeServer).GetField("_clients", Flags).GetValue(server)).Add(expiredSubscriber);
            module.Handle(new BridgeRequest { id = "expired-replay", method = "market.candles.subscribe", @params = new RequestParams() }, expiredSubscriber);
            typeof(ScreenStocksRealtimeClient).GetMethod("SetReady", Flags).Invoke(realtime, new object[] { false });
            candles.Advance(25000); // controlled synchronized-watermark advancement during fallback
            module.Update();
            Require(Frames(expiredSubscriber).Any(x => x.Contains("\"event\":\"market.candle_gap\"")), "expired paused replay requests explicit resubscription");
            VerifyLossAccounting();
            Console.WriteLine("PASS: native price hooks, exact raw baseline bootstrap, separate candle replay, fallback and payout confirmation");
        }
        finally { module.Dispose(); stockSingleton.SetValue(null, savedStock); }
    }

    private static void VerifyPayoutConfirmation(MarketStreamModule module, ScreenStocksRealtimeClient realtime, BridgeConnection connection)
    {
        var game = New(typeof(GameManager)); MakeUnityAlive(game);
        var intents = typeof(GameManager).GetField("_upgradeIntents", Flags);
        intents.SetValue(game, Activator.CreateInstance(intents.FieldType));
        Set(game, "_appliedInitialServerState", true); Set(game, "_lastSeenDataEpoch", 7L); Set(game, "_lastSeenDividendBoundary", 0L);
        var payout = new RealtimePassivePayoutData { dataEpoch = 7, boundary = 1, dividendAmount = new BigNumberWire { m = 25, e = 0 }, requiresStateRefresh = true };
        var method = typeof(GameManager).GetMethod("HandleRealtimePassivePayout", Flags);
        WithRaw(realtime, "{\"type\":\"passivePayout\"}", () => method.Invoke(game, new object[] { payout }));
        WithRaw(realtime, "{\"type\":\"passivePayout\"}", () => method.Invoke(game, new object[] { payout }));
        Require(Frames(connection).Count(x => x.Contains("\"event\":\"market.dividend_paid\"")) == 1, "confirmed payout deduplicated by epoch/boundary");
        Require(Frames(connection).Any(x => x.Contains("\"dividendAmount\":\"25\"")), "authoritative dividend amount is exact");
        payout.dataEpoch = 6; payout.boundary = 99;
        WithRaw(realtime, "{\"type\":\"passivePayout\"}", () => method.Invoke(game, new object[] { payout }));
        Require(Frames(connection).Count(x => x.Contains("\"event\":\"market.dividend_paid\"")) == 1, "obsolete data epoch cannot confirm a payout");
    }

    private static void VerifyConnectedSubscriptions(MarketStreamModule module, ScreenStocksRealtimeClient realtime, BridgeServer server)
    {
        var observed = Connection(); var connected = Connection();
        var clients = (List<BridgeConnection>)typeof(BridgeServer).GetField("_clients", Flags).GetValue(server);
        clients.Add(observed); clients.Add(connected);
        module.Handle(new BridgeRequest { id = "observed", method = "market.candles.subscribe", @params = new RequestParams { candleMode = "" } }, observed);
        module.Handle(new BridgeRequest { id = "connected", method = "market.candles.subscribe", @params = new RequestParams { candleMode = "connected" } }, connected);
        var ordinary = (CandleStream)typeof(MarketStreamModule).GetField("_candles", Flags).GetValue(module);
        var joined = (CandleStream)typeof(MarketStreamModule).GetField("_connectedCandles", Flags).GetValue(module);
        ordinary.Advance(4000); joined.Advance(4000); module.Update();
        Require(Frames(connected).Any(x => x.Contains("\"event\":\"market.candle\"") && x.Contains("\"bucketStartMs\":2000") && x.Contains("\"open\":10,")),
            "connected subscription replays previous close as open through native accepted feed");
        Require(Frames(observed).Where(x => x.Contains("\"event\":\"market.candle\"")).All(x => x.Contains("\"candleMode\":\"observed\"")), "observed subscribers never receive connected bars");
        Require(Frames(connected).Where(x => x.Contains("\"event\":\"market.candle\"")).All(x => x.Contains("\"candleMode\":\"connected\"")), "connected subscribers never receive observed bars");
        Require(Frames(connected).Any(x => x.Contains("\"candleGeneration\":\"" + joined.Generation + "\"")), "connected feed status uses selected generation");
        var observedCount = Frames(observed).Count(x => x.Contains("\"event\":\"market.candle\""));
        module.Accept(realtime, "{\"type\":\"priceVector\"}", "L", new[] { "A" }, new float[] { 8 }, 2900, 4);
        Require(Frames(connected).Any(x => x.Contains("\"bucketStartMs\":3000") && x.Contains("\"revision\":2") && x.Contains("\"open\":8,")), "late predecessor close revises live connected successor");
        Require(Frames(observed).Count(x => x.Contains("\"event\":\"market.candle\"")) == observedCount + 1, "observed mode has no synthetic successor correction");
        module.Handle(new BridgeRequest { id = "wrong-mode-cursor", method = "market.candles.subscribe", @params = new RequestParams { generation = joined.Generation, afterSequence = joined.Sequence } }, observed);
        Require(Frames(observed).Any(x => x.Contains("\"id\":\"wrong-mode-cursor\"") && x.Contains("\"gap\":true")), "foreign mode cursor requests resynchronization");
        module.Handle(new BridgeRequest { id = "invalid-mode", method = "market.candles.subscribe", @params = new RequestParams { candleMode = "invented" } }, connected);
        Require(Frames(connected).Any(x => x.Contains("\"id\":\"invalid-mode\"") && x.Contains("invalid_params")), "invalid mode rejected without altering active subscription");
        Require(connected.ConnectedCandles, "invalid request preserves connected mode");
    }

    private static void VerifyLossAccounting()
    {
        var delivered = Connection();
        var sink = new ClosingMemoryStream(delivered.Close);
        Set(delivered, "<Stream>k__BackingField", sink);
        var sourceJson = "{ \"type\": \"prices\" }";
        var rawData = "{\"sourceJson\":" + BridgeJson.Quote(sourceJson) + "}";
        delivered.SendStream("market.price", rawData);
        typeof(BridgeConnection).GetMethod("WriteLoop", Flags).Invoke(delivered, null);
        var frame = System.Text.Encoding.UTF8.GetString(sink.ToArray()).TrimEnd();
        Require(JsonObjectParser.ExtractTopLevelObject(frame, "data") == rawData, "writer preserves source payload under data");
        var delivery = JsonObjectParser.ExtractTopLevelObject(frame, "delivery");
        Require(delivery != null && delivery.Contains("\"sequence\":1") && delivery.Contains("\"droppedEvents\":0"), "delivery metadata lives outside source payload");
        var connection = Connection();
        for (var i = 0; i < 64; i++) connection.SendStream("market.price", "{}");
        connection.SendStream("market.clock", "{}");
        Require(!connection.IsClosed, "non-activity market delivery remains best effort");
        var loss = (string)typeof(BridgeConnection).GetMethod("LossJson", Flags).Invoke(connection, null);
        Require(loss.Contains("\"market.price\":1"), "drop charged to evicted stream rather than incoming stream");
        Require((bool)typeof(BridgeConnection).GetField("_lossPending", Flags).GetValue(connection), "quiet-stream loss notification bypasses bounded queue");
        var protectedConnection = Connection(); protectedConnection.IsActivitySubscribed = true;
        for (var i = 0; i < 65; i++) protectedConnection.SendStream("market.price", "{}");
        Require(protectedConnection.IsClosed, "mixed activity/market connection retains disconnect-on-overflow");
    }

    private sealed class ClosingMemoryStream : System.IO.MemoryStream
    {
        private readonly Action _closed;
        internal ClosingMemoryStream(Action closed) { _closed = closed; }
        public override void Flush() { base.Flush(); _closed(); }
    }

    private static void WithRaw(ScreenStocksRealtimeClient realtime, string json, Action native)
    {
        var args = new object?[] { realtime, json, null };
        typeof(MarketStreamModule).GetMethod("RawPrefix", Static).Invoke(null, args);
        try { native(); }
        finally { typeof(MarketStreamModule).GetMethod("RawFinalizer", Static).Invoke(null, new object?[] { null, args[2] }); }
    }
    private static BridgeConnection Connection()
    {
        var connection = (BridgeConnection)New(typeof(BridgeConnection));
        foreach (var name in new[] { "_outbound", "_streamSequences", "_streamDrops" })
        { var field = typeof(BridgeConnection).GetField(name, Flags); field.SetValue(connection, Activator.CreateInstance(field.FieldType)); }
        Set(connection, "_sendGate", new object()); Set(connection, "_wake", new System.Threading.AutoResetEvent(false));
        Set(connection, "_onClosed", new Action<BridgeConnection>(_ => { }));
        Set(connection, "<SessionId>k__BackingField", Guid.NewGuid().ToString("N"));
        return connection;
    }
    private static List<string> Frames(BridgeConnection connection) => ((IEnumerable)typeof(BridgeConnection).GetField("_outbound", Flags).GetValue(connection))
        .Cast<object>().Select(frame => (string)frame.GetType().GetField("Json", Flags).GetValue(frame)).ToList();
    private static object New(Type type) => FormatterServices.GetUninitializedObject(type);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static void MakeUnityAlive(object value)
    {
        for (var type = value.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField("m_CachedPtr", Flags); if (field != null) { field.SetValue(value, new IntPtr(1)); return; } }
        throw new MissingFieldException("Unity cached pointer");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Market stream regression: " + message); }
}
