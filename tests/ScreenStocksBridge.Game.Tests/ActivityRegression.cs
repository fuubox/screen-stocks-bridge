using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using ScreenStocks.Net;
using ScreenStocksBridge;

// Exercises the production activity consumer seam with actual native bucket
// application, delta replacement/pruning and history alignment. No scene or HTTP.
internal static class ActivityRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Apply = typeof(HumanActivityClient).GetMethod("Apply", Private);
    private static readonly MethodInfo Delta = typeof(HumanActivityClient).GetMethod("HandleRealtimeActivityDelta", Private);

    internal static void Run()
    {
        var client = (HumanActivityClient)FormatterServices.GetUninitializedObject(typeof(HumanActivityClient));
        Set(client, "_buckets", new List<HumanActivityBucket>());
        Set(client, "_alignedTickSnapshot", new List<long>());
        Set(client, "_focusedStockId", "A");
        var ticks = Enumerable.Range(0, 100).Select(i => (long)i * 10).ToArray();
        var response = new HumanActivityResponse { stockId = "A", ticksPerBucket = 10, fullScaleImpact = 0.2,
            buckets = new List<HumanActivityBucketDto> {
                Bucket(0, 1, 0, 0.01, 0), Bucket(1, 0, 2, 0, 0.02), Bucket(2, 0, 0, 0, 0),
                Bucket(-1, 100, 0, 1, 0), Bucket(3, 1, 0, double.NaN, 0) } };
        Apply.Invoke(client, new object[] { response });
        var feed = new ActivityFeed(() => 0, _ => { });
        var events = new List<string>();
        feed.Changed += frames => events.AddRange(frames);
        Publish(client, ticks, feed);
        var rows = ActivityModule.CaptureNative(client, "A", ticks, out _);
        Require(rows.Count == 103, "invalid bucket rejection / zero-filled history");
        Require(rows["sample:0"].Contains("\"up\":1"), "native bucket placement at preceding position");
        Require(rows["bucket:2:0"].Contains("\"placementSampleTick\":20"), "explicit zero bucket placement");
        Require(rows["sample:900"].Contains("\"total\":0"), "unplaced history is zero-filled");
        var snapshot = feed.Snapshot("reader", "", 0);
        Require(snapshot.Contains("\"revision\":1"), "wire snapshot uses native capture");
        var before = events.Count;
        Delta.Invoke(client, new object[] { new HumanActivityDelta { stockId = "A", ticksPerBucket = 10,
            fullScaleImpact = 0.2, oldestRetainedBucketId = 1, buckets = new List<HumanActivityBucketDto> { Bucket(1, 5, 0, 0.05, 0) } } });
        Publish(client, ticks, feed);
        var changed = string.Join("", events.Skip(before));
        Require(changed.Contains("\"op\":\"remove\",\"key\":\"bucket:0:0\""), "native pruning published");
        Require(changed.Contains("\"key\":\"sample:10\""), "revision older than latest 32 published");
        Require(changed.Contains("\"up\":5"), "delta replaces rather than adds counts");
        var count = events.Count;
        Delta.Invoke(client, new object[] { new HumanActivityDelta { stockId = "other", ticksPerBucket = 10,
            buckets = new List<HumanActivityBucketDto> { Bucket(1, 99, 0, 1, 0) } } });
        Publish(client, ticks, feed);
        Require(events.Count == count, "wrong-stock delta rejected by native processing");

        var sparse = new long[] { 0, 100, 200 };
        Apply.Invoke(client, new object[] { new HumanActivityResponse { stockId = "A", ticksPerBucket = 10,
            fullScaleImpact = -1, buckets = new List<HumanActivityBucketDto> { Bucket(1, 2, 0, 0.01, 0), Bucket(2, 3, 0, 0.02, 0) } } });
        rows = ActivityModule.CaptureNative(client, "A", sparse, out _);
        Require(rows["sample:0"].Contains("\"up\":5"), "multiple buckets aggregate at one sparse position");
        Require(client.FullScaleImpactFraction == 0.2, "native invalid-scale fallback");

        Apply.Invoke(client, new object[] { new HumanActivityResponse { stockId = "A", ticksPerBucket = 10,
            fullScaleImpact = 0.2, buckets = new List<HumanActivityBucketDto> {
                Bucket(1, 2, 0, 0.01, 0), Bucket(1, 3, 0, 0.02, 0), Bucket(500, 4, 0, 0.03, 0) } } });
        rows = ActivityModule.CaptureNative(client, "A", ticks, out _);
        Require(rows.ContainsKey("bucket:1:0") && rows.ContainsKey("bucket:1:1"), "duplicate accepted IDs preserved as separate occurrences");
        Require(rows["bucket:1:1"].Contains("\"placementSampleTick\":null"), "duplicate suppression reflected in placement metadata");
        Require(rows["bucket:500:0"].Contains("\"placementSampleTick\":null"), "outside-history bucket has no placement");

        VerifyHistogramInputs();
        VerifyInstalledHooks(client, ticks);
        VerifyQuietBackpressure();
        Console.WriteLine("PASS: native activity application, pruning, placement, stream revisions and histogram inputs");
    }

    private static void VerifyQuietBackpressure()
    {
        var connection = (BridgeConnection)FormatterServices.GetUninitializedObject(typeof(BridgeConnection));
        var closed = 0;
        Set(connection, "_sendGate", new object());
        var queue = typeof(BridgeConnection).GetField("_outbound", Private);
        queue.SetValue(connection, Activator.CreateInstance(queue.FieldType));
        Set(connection, "_wake", new System.Threading.AutoResetEvent(false));
        Set(connection, "_onClosed", new Action<BridgeConnection>(_ => closed++));
        connection.IsActivitySubscribed = true;
        for (var i = 0; i < 64; i++) connection.SendActivity("{}");
        Require(!connection.IsClosed, "bounded queue accepts its capacity");
        connection.SendActivity("{}");
        Require(connection.IsClosed && closed == 1, "quiet stream overflow closes immediately rather than dropping a chunk");
    }

    private static void VerifyInstalledHooks(HumanActivityClient client, long[] ticks)
    {
        var manager = FormatterServices.GetUninitializedObject(typeof(StockManager));
        var source = FormatterServices.GetUninitializedObject(typeof(RemoteMarketDataSource));
        Set(source, "<IsReady>k__BackingField", true);
        Set(source, "_historySampleTicks", new Dictionary<string, List<long>> { ["A"] = ticks.ToList() });
        Set(manager, "_source", source); Set(manager, "_remoteSource", source); Set(manager, "_usingRemote", true);
        Set(manager, "_humanActivityClient", client); Set(manager, "_chartActivityFocus", "A");
        // Make Unity's managed null check recognize the synthetic manager without
        // invoking an engine icall. No pointer is dereferenced by these methods.
        var unityType = typeof(StockManager).BaseType;
        while (unityType != null && unityType.GetField("m_CachedPtr", Private) == null) unityType = unityType.BaseType;
        (unityType ?? throw new MissingFieldException("Unity cached pointer")).GetField("m_CachedPtr", Private).SetValue(manager, new IntPtr(1));
        var singleton = typeof(StockManager).GetField("<I>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        var saved = singleton.GetValue(null);
        singleton.SetValue(null, manager);
        var module = new ActivityModule();
        try
        {
            module.Install();
            var feed = (ActivityFeed)typeof(ActivityModule).GetField("_feed", Private).GetValue(module);
            Apply.Invoke(client, new object[] { new HumanActivityResponse { stockId = "A", ticksPerBucket = 10,
                lastTick = 990, fullScaleImpact = 0.2, buckets = new List<HumanActivityBucketDto> { Bucket(1, 2, 0, 0.01, 0) } } });
            var first = feed.Snapshot("test", "", 0);
            Require(first.Contains("\"lastResponseAtUtc\":\""), "accepted native full response receipt captured");
            var revision = feed.Revision;
            Delta.Invoke(client, new object[] { new HumanActivityDelta { stockId = "other", ticksPerBucket = 10,
                lastTick = 991, oldestRetainedBucketId = 1, buckets = new List<HumanActivityBucketDto>() } });
            Require(feed.Revision == revision, "rejected native delta does not advance source metadata");
            Delta.Invoke(client, new object[] { new HumanActivityDelta { stockId = "A", ticksPerBucket = 10,
                lastTick = 992, oldestRetainedBucketId = 1, fullScaleImpact = 0.2,
                buckets = new List<HumanActivityBucketDto> { Bucket(1, 4, 0, 0.02, 0) } } });
            Require(feed.Revision > revision, "accepted native delta updates stream immediately");
            Require(feed.Snapshot("test", "", 0).Contains("\"oldestRetainedBucketId\":1"), "native retention bound captured");
            var valueTime = typeof(ActivityModule).GetField("_lastValueChangeUtc", Private).GetValue(module);
            Delta.Invoke(client, new object[] { new HumanActivityDelta { stockId = "A", ticksPerBucket = 10,
                lastTick = 993, oldestRetainedBucketId = 1, fullScaleImpact = 0.2,
                buckets = new List<HumanActivityBucketDto>() } });
            Require(Equals(valueTime, typeof(ActivityModule).GetField("_lastValueChangeUtc", Private).GetValue(module)), "unchanged accepted response does not claim a value change");
            var oldGeneration = feed.Generation;
            typeof(HumanActivityClient).GetMethod("HandleRealtimeConnectionChanged", Private).Invoke(client, new object[] { false });
            module.Update();
            Require(oldGeneration != feed.Generation, "native connection transition invalidates old stream generation");
            Require(feed.Snapshot("test", "", 0).Contains("\"lastResponseAtUtc\":null"), "source receipt evidence is unknown after reconnect transition");
            Set(client, "_disposed", true); // suppress any fetch while testing native focus routing
            feed.Pin("owner", "B", false);
            var generation = feed.Generation;
            typeof(StockManager).GetMethod("SetChartActivityFocus").Invoke(manager, new object[] { "C" });
            Require((string)typeof(HumanActivityClient).GetField("_focusedStockId", Private).GetValue(client) == "B", "pin intercepts navigation before native subscription switch");
            Require(feed.Generation == generation, "pinned navigation does not clear source data");
            feed.Unpin("owner");
            Require((string)typeof(HumanActivityClient).GetField("_focusedStockId", Private).GetValue(client) == "C", "unpin follows currently displayed graph");
        }
        finally { module.Dispose(); singleton.SetValue(null, saved); }
    }

    private static void Publish(HumanActivityClient client, long[] ticks, ActivityFeed feed)
    {
        var rows = ActivityModule.CaptureNative(client, "A", ticks, out var signature);
        feed.Publish("{\"nativeSignature\":" + BridgeJson.Quote(signature) + "}", rows);
    }

    private static void VerifyHistogramInputs()
    {
        // No Graphic/Canvas constructor is run. Group math uses only these fields.
        var graphType = typeof(StockManager).Assembly.GetType("UILineGraph", true);
        var graph = FormatterServices.GetUninitializedObject(graphType);
        Set(graph, "_humanActivity", new HumanActivitySample[] { new HumanActivitySample(1, 0, 0.01, 0), new HumanActivitySample(0, 2, 0, 0.02) });
        Set(graph, "_visibleDataCount", 2);
        Set(graph, "_visiblePriceStartIndex", 0);
        // TryGetActivityForPriceIndex offsets activity by the full price count.
        Set(graph, "_retainedPriceCount", 2);
        var group = graphType.GetMethod("GetActivityGroupImpact", Private);
        var a = new object[] { 0, 1, 0d }; var b = new object[] { 1, 1, 0d };
        var first = (double)group.Invoke(graph, a); var second = (double)group.Invoke(graph, b);
        Require(Math.Abs(first - 0.01) < 1e-10 && Math.Abs(second - 0.02) < 1e-10, "native grouped impact totals");
        Require(Math.Abs(first / Math.Max(first, second) - 0.5) < 1e-10, "visible maximum normalization differs from server reference");
        Require((double)a[2] > 0 && (double)b[2] < 0, "native direction comes from grouped net impact");
        Set(graph, "_humanActivity", new HumanActivitySample[] { new HumanActivitySample(1, 0, 0, 0), new HumanActivitySample(0, 2, 0, 0) });
        var counts = graphType.GetMethod("GetActivityGroupCount", Private);
        a = new object[] { 0, 2, 0d };
        Require((double)counts.Invoke(graph, a) == 3 && (double)a[2] == -1, "native count fallback sums total and net counts");
    }

    private static HumanActivityBucketDto Bucket(long id, long up, long down, double upImpact, double downImpact)
        => new HumanActivityBucketDto { bucketId = id, up = up, down = down, upImpact = upImpact, downImpact = downImpact };
    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Private) ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Activity parity regression: " + message); }
}
