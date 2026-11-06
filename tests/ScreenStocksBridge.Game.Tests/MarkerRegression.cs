using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using ScreenStocksBridge;
using ScreenStocks.Net;
using UnityEngine;
using UnityEngine.UI;

// Exercises the native renderer and reconciliation offline; only circle drawing
// is intercepted so no Unity Canvas is required.
public static class MarkerRegression
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly List<Vector2> Centers = new List<Vector2>();
    static int _failures;
    static long _nowMs = 1000500;
    static float _currentPrice = 70;
    static bool _throwCircle;
    public static int Run()
    {
        var harness = new Harmony("screenstocks.marker.regression");
        var enabled = PurchaseMarkerUnstackHarmonyPatch.Enabled;
        _failures = 0;
        try
        {
            harness.Patch(AccessTools.Method(typeof(UILineGraph), "DrawCircle"), prefix: new HarmonyMethod(typeof(MarkerRegression), nameof(Circle)));
            PurchaseMarkerUnstackHarmonyPatch.Enabled = true;
            PurchaseMarkerUnstackHarmonyPatch.Install(harness);
            DrawCase("two coincident buys", new[] { Marker(true, false), Marker(true, false) }, true);
            DrawCase("three coincident buys", new[] { Marker(true, false), Marker(true, false), Marker(true, false) }, true);
            DrawCase("one buy and one sell", new[] { Marker(true, false), Marker(false, false) }, true);
            DrawCase("short and short cover", new[] { Marker(false, true), Marker(true, true) }, true, true);
            var edgeA = Marker(true, false); edgeA.DataIndex = 103;
            var edgeB = Marker(true, false); edgeB.DataIndex = 104;
            DrawCase("native right-edge spillover", new[] { edgeA, edgeB }, true);
            DrawCase("eight coincident buys", Enumerable.Range(0, 8).Select(_ => Marker(true, false)).ToArray(), true);
            var low = Marker(true, false); low.Price = 0;
            DrawCase("bottom-edge buys", new[] { low, low, low }, true);
            var high = Marker(false, false); high.Price = 100;
            DrawCase("top-edge sells", new[] { high, high, high }, true);
            DrawCase("overcrowded plot retains all dots", Enumerable.Range(0, 20).Select(_ => Marker(true, false)).ToArray(), false);
            var isolated = Marker(true, false); isolated.Price = 80;
            DrawCase("isolated dots stay put", new[] { Marker(true, false), isolated }, false, unchanged: true);
            PurchaseMarkerUnstackHarmonyPatch.Enabled = false;
            DrawCase("disabled patch keeps native positions", new[] { Marker(true, false), Marker(true, false) }, false, unchanged: true);
            PurchaseMarkerUnstackHarmonyPatch.Enabled = true;
            DrawCase("exception restores original data", new[] { Marker(true, false), Marker(true, false) }, false, throwDraw: true);
            var nearby = Marker(true, false); nearby.DataIndex = 51;
            DrawCase("nearby circles separate without horizontal shifts", new[] { Marker(true, false), nearby }, true);
            var distant = Marker(true, false); distant.DataIndex = 60;
            DrawCase("nearby ticks without visual overlap stay put", new[] { Marker(true, false), distant }, false, unchanged: true);
            AutoCase(harness);

            Console.WriteLine("MARKER REGRESSION SUMMARY: " + _failures + " failed expectations");
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        finally { harness.UnpatchSelf(); PurchaseMarkerUnstackHarmonyPatch.Enabled = enabled; AutoActionGraphMarkerHarmonyPatch.Clear(); }
    }
    static bool Circle(Vector2 __1) { Centers.Add(__1); if (_throwCircle) throw new InvalidOperationException("controlled draw failure"); return false; }
    static UILineGraph.PurchaseMarker Marker(bool buy, bool shortTrade)
        => new UILineGraph.PurchaseMarker { DataIndex = 50, Price = 50, IsBuy = buy, IsShort = shortTrade };
    static void DrawCase(string name, UILineGraph.PurchaseMarker[] values, bool requireSeparation, bool coverBelow = false, bool unchanged = false, bool throwDraw = false)
    {
        var graph = (UILineGraph)New(typeof(UILineGraph)); Alive(graph);
        var markers = values.ToList();
        Set(graph, "_purchaseMarkers", markers); Set(graph, "_visibleDataCount", 100); Set(graph, "_visiblePointCount", 100);
        Set(graph, "_firstVisibleTick", 0L); Set(graph, "_minValue", 0f); Set(graph, "_maxValue", 100f);
        Set(graph, "_purchaseMarkerRadius", 5f); Set(graph, "_purchaseMarkerStrokeWidth", 1f);
        Centers.Clear();
        var threw = false;
        _throwCircle = throwDraw;
        try { typeof(UILineGraph).GetMethod("DrawPurchaseMarkers", Fields).Invoke(graph, new object?[] { null, new Rect(0, 0, 200, 100) }); }
        catch (TargetInvocationException ex) when (throwDraw && ex.InnerException is InvalidOperationException) { threw = true; }
        finally { _throwCircle = false; }
        if (throwDraw)
        {
            Check(threw && Centers.Count == 1 && Centers[0].y != values[0].Price && markers.Select((m,i) => m.Price == values[i].Price && m.DataIndex == values[i].DataIndex && m.IsBuy == values[i].IsBuy && m.IsShort == values[i].IsShort).All(ok => ok), name, "after native draw throws");
            return;
        }
        if (unchanged) Check(Centers.Select((point, i) => Math.Abs(point.y - values[i].Price) < .001).All(ok => ok), name, "native vertical positions");
        Check(Centers.Count == values.Length && Centers.Select((p,i) => Math.Abs(p.x - Math.Min(values[i].DataIndex, 99) * (200f / 99)) < .001).All(ok => ok), name + " keeps original horizontal positions", string.Join(";", Centers.Select(p => p.x + "," + p.y)));
        if (requireSeparation) Check(Centers.Count == values.Length && Centers.OrderBy(p => p.y).Zip(Centers.OrderBy(p => p.y).Skip(1), (a,b) => b.y - a.y).All(gap => gap >= 10 - .001),
            name + " has non-overlapping vertical circles", "centers=" + string.Join(",", Centers.Select(p => p.y)) + " diameter=10");
        Check(Centers.All(p => p.y >= 0 && p.y <= 100), name + " stays visible", "within plot");
        if (name == "three coincident buys") Check(Centers.All(p => p.y < 50), "buy lanes prefer below execution", "centers=" + string.Join(",", Centers.Select(p => p.y)));
        if (coverBelow) Check(Centers[1].y < 50, "short cover stays buy-side (below its execution price)", "cover y=" + Centers[1].y);
        Check(markers.Select((m,i) => m.Price == values[i].Price && m.DataIndex == values[i].DataIndex && m.IsBuy == values[i].IsBuy && m.IsShort == values[i].IsShort).All(ok => ok), name + " restores original marker data", "after draw");
    }
    static bool ClockNow(ref long __result) { __result = _nowMs; return false; }
    static bool ClockSynced(ref bool __result) { __result = true; return false; }
    static bool Confirm(ref bool __result) { __result = true; return false; }
    static bool CurrentPrice(ref float __result) { __result = _currentPrice; return false; }
    static void AutoCase(Harmony harness)
    {
        harness.Patch(AccessTools.PropertyGetter(typeof(ScreenStocks.Net.ServerClock), "NowMs"), prefix: new HarmonyMethod(typeof(MarkerRegression), nameof(ClockNow)));
        harness.Patch(AccessTools.PropertyGetter(typeof(ScreenStocks.Net.ServerClock), "IsSynchronized"), prefix: new HarmonyMethod(typeof(MarkerRegression), nameof(ClockSynced)));
        harness.Patch(AccessTools.Method(typeof(StockManager), "GetCurrentPrice"), prefix: new HarmonyMethod(typeof(MarkerRegression), nameof(CurrentPrice)));
        AutoActionGraphMarkerHarmonyPatch.Install(harness);
        var manager = (StockManager)New(typeof(StockManager)); Alive(manager);
        var source = (RemoteMarketDataSource)New(typeof(RemoteMarketDataSource));
        var ticks = Enumerable.Range(901, 100).Select(i => (long)i).ToList();
        var history = Enumerable.Range(0, 100).Select(i => (float)(20 + i)).ToList();
        history[98] = 42;
        Set(source, "_history", new Dictionary<string, List<float>> { ["A"] = history });
        Set(source, "_historySampleTicks", new Dictionary<string, List<long>> { ["A"] = ticks });
        Set(source, "_historySampleCount", new Dictionary<string, long> { ["A"] = 100 });
        Set(source, "<ServerTick>k__BackingField", 1000L);
        Set(source, "_serverUnixSecondsAtAnchor", 1000d); Set(source, "_realtimeSecondsAtAnchor", 0d); Set(source, "_hasServerClockAnchor", true);
        var state = (ClientStockState)New(typeof(ClientStockState));
        if (state.Markers == null) state.Markers = new List<UILineGraph.PurchaseMarker>();
        Set(manager, "_source", source); Set(manager, "_stockStates", new Dictionary<string, ClientStockState> { ["A"] = state });
        var singleton = AccessTools.Field(typeof(StockManager), "<I>k__BackingField");
        var saved = singleton.GetValue(null); singleton.SetValue(null, manager);
        var gameSingleton = AccessTools.Field(typeof(GameManager), "<I>k__BackingField");
        var savedGame = gameSingleton.GetValue(null);
        var game = (GameManager)New(typeof(GameManager)); Alive(game);
        var settings = (GameSettingsSO)New(typeof(GameSettingsSO)); Alive(settings);
        settings.tickRateMs = 100; settings.historyLength = 100; settings.chartVisibleTicks = 100;
        Set(game, "_settings", settings); gameSingleton.SetValue(null, game);
        try
        {
            var auto = (AutoActionManager)New(typeof(AutoActionManager));
            Set(auto, "<Predictions>k__BackingField", new AutoActionPredictionLedger());
            foreach (var delay in new[] { 0, 500, 1000 })
            {
                state.Markers.Clear(); AutoActionGraphMarkerHarmonyPatch.Clear(); SeedPrices(source, 1000, 1000000);
                _nowMs = 1000000 + delay;
                var old = Action(0); var current = Action(999900);
                typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarkers", Fields).Invoke(auto,
                    new object[] { new[] { old }, new[] { current } });
                Check(state.Markers.Count == 1, "native reconciliation preserves marker (delay=" + delay + "ms)", "count=" + state.Markers.Count);
                var marker = state.Markers[0];
                // Price-history local index98 maps to graph dataIndex99: the
                // actual SetData origin is total-visible+1, not total-visible.
                Check(marker.DataIndex == 99 && Math.Abs(marker.Price - 42) < .001,
                    "execution sample/price invariant to confirmation delay=" + delay + "ms",
                    "expected local sample=98 graph index=99 price=42; actual graph index=" + marker.DataIndex + " price=" + marker.Price);
            }
            _nowMs = 1000000;
            state.Markers.Clear(); AutoActionGraphMarkerHarmonyPatch.Clear(); SeedPrices(source, 1000, 1000000);
            typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarkers", Fields).Invoke(auto,
                new object[] { new[] { Action(0) }, new[] { Action(990100) } });
            var oldest = state.Markers[0];
            var graph = (UILineGraph)New(typeof(UILineGraph)); Alive(graph);
            Set(graph, "_purchaseMarkers", state.Markers); Set(graph, "_visibleDataCount", 100); Set(graph, "_visiblePointCount", 100);
            // Exactly the origin calculated by the installed native SetData.
            Set(graph, "_firstVisibleTick", 100L - 100 + 1);
            Set(graph, "_minValue", 0f); Set(graph, "_maxValue", 130f);
            Set(graph, "_purchaseMarkerRadius", 5f); Set(graph, "_purchaseMarkerStrokeWidth", 1f);
            Centers.Clear();
            typeof(UILineGraph).GetMethod("DrawPurchaseMarkers", Fields).Invoke(graph, new object?[] { null, new Rect(0, 0, 200, 100) });
            Check(Centers.Count == 1, "corrected marker at oldest visible sample stays visible",
                "stored markers=" + state.Markers.Count + " graph index=" + oldest.DataIndex + " first visible=1 drawn=" + Centers.Count);
            void Reconcile(long timestamp)
            {
                state.Markers.Clear();
                typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarkers", Fields).Invoke(auto,
                    new object[] { new[] { Action(0) }, new[] { Action(timestamp) } });
            }
            void NativeUnchanged(string name)
            {
                Check(state.Markers.Count == 1 && state.Markers[0].DataIndex == 99 && state.Markers[0].Price == 70,
                    name, "unmappable evidence preserves native marker");
            }
            Reconcile(1); NativeUnchanged("opaque execution marker is not guessed");
            SeedPrices(source, 1001, 0);
            Reconcile(999900); NativeUnchanged("missing quote timestamp invalidates old anchor");
            SeedPrices(source, 1000, 1000000);
            Reconcile(999900); NativeUnchanged("rejected stale quote cannot revive old anchor");
            SeedPrices(source, 1001, 1000100);
            Reconcile(999900);
            Check(state.Markers.Count == 1 && state.Markers[0].DataIndex == 99 && state.Markers[0].Price == 42,
                "fresh accepted anchor restores correction", "nearest retained sample");
            Set(source, "_historySampleCount", new Dictionary<string, long> { ["A"] = 1000 });
            Reconcile(999900);
            Check(state.Markers[0].DataIndex == 999 && state.Markers[0].Price == 42,
                "rolled history matches native global index", "total1000 history100 local98 maps999");
            Set(source, "_historySampleCount", new Dictionary<string, long> { ["A"] = 100 });
            Action<long> heartbeat = _ => Reconcile(999900);
            source.OnHeartbeat += heartbeat;
            try { SeedPrices(source, 1002, 1000200); }
            finally { source.OnHeartbeat -= heartbeat; }
            Check(state.Markers[0].Price == 42, "confirmation during native heartbeat uses the accepted quote", "anchor available inside update callbacks");
            var confirmMethod = AccessTools.Method(typeof(AutoActionPredictionLedger), "TryConfirm");
            harness.Patch(confirmMethod, prefix: new HarmonyMethod(typeof(MarkerRegression), nameof(Confirm)));
            try
            {
                state.Markers.Clear(); AutoActionGraphMarkerHarmonyPatch.Clear(); SeedPrices(source, 1002, 1000200);
                var add = typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarker", BindingFlags.Static | Fields);
                add.Invoke(null, new object[] { Action(0), 45f });
                add.Invoke(null, new object[] { Action(0, 1), 46f });
                typeof(AutoActionManager).GetMethod("AddRemoteExecutionMarkers", Fields).Invoke(auto,
                    new object[] { new[] { Action(0), Action(0, 1) }, new[] { Action(999900), Action(999800, 1) } });
                Check(state.Markers.Count == 2 && state.Markers[0].DataIndex == 99 && state.Markers[0].Price == 42 &&
                    state.Markers[1].DataIndex == 98 && state.Markers[1].Price == 117,
                    "confirmed predictions retain slot ownership without duplicate dots", "two slots map to their own execution samples");
            }
            finally { harness.Unpatch(confirmMethod, HarmonyPatchType.Prefix, harness.Id); }
            settings.tickRateMs = 0;
            Reconcile(999900); NativeUnchanged("invalid tick duration preserves native marker");
            settings.tickRateMs = 100;
            AutoActionGraphMarkerHarmonyPatch.Clear();
            Reconcile(999900); NativeUnchanged("option reset waits for a new quote anchor");
        }
        finally { singleton.SetValue(null, saved); gameSingleton.SetValue(null, savedGame); }
    }
    static void SeedPrices(RemoteMarketDataSource source, long tick, long time)
    {
        // Empty stocks exercise native acceptance/clock anchoring while retaining
        // the controlled history already installed in the fixture.
        typeof(RemoteMarketDataSource).GetMethod("ApplyPollResult", Fields).Invoke(source,
            new object[] { ApiResult<PricesResponse>.Ok(new PricesResponse {
                tick = tick, serverTimeMs = time, stocks = new List<WireStockPrice>() }) });
    }
    static AutoAction Action(long marker, int slot = 0) => new AutoAction { slotIndex = slot, stockId = "A", actionType = AutoActionType.Buy,
        condition = ActionCondition.Above, targetPrice = 40, enabled = true, lastExecutionMarker = marker };
    static void Check(bool ok, string name, string details)
    { Console.WriteLine((ok ? "PASS: " : "FAIL: ") + name + " | " + details); if (!ok) _failures++; }
    static object New(Type type) => FormatterServices.GetUninitializedObject(type);
    static void Set(object value, string name, object data) => value.GetType().GetField(name, Fields).SetValue(value, data);
    static void Alive(object value)
    {
        for (var type = value.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField("m_CachedPtr", Fields); if (field != null) { field.SetValue(value, new IntPtr(1)); return; } }
    }
}
