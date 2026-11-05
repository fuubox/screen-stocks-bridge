using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ScreenStocks.Net;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class MarketStreamModule : IDisposable
    {
        private static readonly FieldInfo RealtimeField = AccessTools.Field(typeof(RemoteMarketDataSource), "_realtime");
        private static readonly FieldInfo LayoutIds = AccessTools.Field(typeof(ScreenStocksRealtimeClient), "_priceLayoutStockIds");
        private static readonly FieldInfo AppliedState = AccessTools.Field(typeof(GameManager), "_appliedInitialServerState");
        private static readonly FieldInfo DataEpoch = AccessTools.Field(typeof(GameManager), "_lastSeenDataEpoch");
        private static readonly FieldInfo DividendBoundary = AccessTools.Field(typeof(GameManager), "_lastSeenDividendBoundary");
        private static MarketStreamModule? Current;
        [ThreadStatic] private static RawContext? _raw;
        private readonly StateService _state;
        private readonly Func<BridgeServer?> _server;
        private readonly Stopwatch _timer = Stopwatch.StartNew();
        private readonly MarketCadence _cadence = new MarketCadence();
        private readonly CandleStream _candles = new CandleStream();
        private readonly Dictionary<BridgeConnection, Queue<CandleUpdate>> _replays = new Dictionary<BridgeConnection, Queue<CandleUpdate>>();
        private Harmony? _harmony;
        private RemoteMarketDataSource? _source;
        private ScreenStocksRealtimeClient? _realtime;
        private string _sourceGeneration = Guid.NewGuid().ToString("N");
        private string? _baseline, _baselineLayout;
        private bool _instant, _websocketActive, _legacyPrices;
        private string _lastMaterial = string.Empty, _lastStatus = string.Empty;
        private string? _lastCaptureError;
        private double _nextClock;
        private long _payoutEpoch = long.MinValue, _payoutBoundary = long.MinValue;
        internal bool Available => _harmony != null;

        internal MarketStreamModule(StateService state, Func<BridgeServer?> server)
        {
            _state = state; _server = server;
            _candles.Updated += update =>
            {
                foreach (var connection in Connections())
                    if (connection.IsCandleSubscribed)
                    {
                        if (_replays.TryGetValue(connection, out var replay))
                        { if (replay.Count >= 65536) connection.Close(); else replay.Enqueue(update); }
                        else connection.SendStream("market.candle", update.Json);
                    }
            };
        }

        internal void Install()
        {
            foreach (var member in new object[] { RealtimeField, LayoutIds, AppliedState, DataEpoch, DividendBoundary })
                if (member == null) throw new MissingMemberException("Required market stream member is missing.");
            Current = this;
            _harmony = new Harmony("screenstocks.bridge.market-streams");
            try
            {
                _harmony.Patch(AccessTools.Method(typeof(ScreenStocksRealtimeClient), "ProcessMessage"),
                    prefix: new HarmonyMethod(typeof(MarketStreamModule), nameof(RawPrefix)),
                    finalizer: new HarmonyMethod(typeof(MarketStreamModule), nameof(RawFinalizer)));
                Patch(typeof(ScreenStocksRealtimeClient), "PublishPriceVector", null, nameof(VectorPostfix));
                Patch(typeof(RemoteMarketDataSource), "HandleRealtimePrices", null, nameof(LegacyPostfix));
                Patch(typeof(ScreenStocksRealtimeClient), "SetReady", nameof(ReadyPrefix), nameof(ReadyPostfix));
                Patch(typeof(GameManager), "HandleRealtimePassivePayout", nameof(PayoutPrefix), nameof(PayoutPostfix));
                Bind();
            }
            catch { Dispose(); throw; }
        }

        private void Patch(Type type, string name, string? prefix, string? postfix) => _harmony!.Patch(
            AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name),
            prefix: prefix == null ? null : new HarmonyMethod(typeof(MarketStreamModule), prefix),
            postfix: postfix == null ? null : new HarmonyMethod(typeof(MarketStreamModule), postfix));

        private static void RawPrefix(ScreenStocksRealtimeClient __instance, string __0, out RawContext? __state)
        { __state = _raw; _raw = new RawContext(__instance, __0); }
        private static Exception? RawFinalizer(Exception? __exception, RawContext? __state)
        { _raw = __state; return __exception; }

        private static void VectorPostfix(ScreenStocksRealtimeClient __instance, RealtimePriceVectorData __0)
        {
            if (_raw == null || !ReferenceEquals(_raw.Client, __instance) || Current == null || __0 == null) return;
            Current.Guard(() =>
            {
                var ids = LayoutIds.GetValue(__instance) as List<string>;
                if (ids == null || __0.prices == null || ids.Count != __0.prices.Count) return;
                Current.Accept(__instance, _raw.Json, __0.layoutRevision, ids, __0.prices, __0.serverTimeMs, __0.tick);
            });
        }

        private static void LegacyPostfix(RemoteMarketDataSource __instance, PricesResponse __0)
        {
            if (_raw == null || Current == null || __0?.stocks == null) return;
            Current.Guard(() =>
            {
                if (!ReferenceEquals(RealtimeField.GetValue(__instance), _raw.Client)) return;
                if (JsonObjectParser.ExtractTopLevelString(_raw.Json, "type") != "prices") return;
                var ids = new List<string>(); var prices = new List<float>();
                foreach (var stock in __0.stocks) { if (stock == null) continue; ids.Add(stock.stockId); prices.Add(stock.price); }
                Current.Accept(_raw.Client, _raw.Json, null, ids, prices, __0.serverTimeMs, __0.tick);
            });
        }

        private static void ReadyPrefix(ScreenStocksRealtimeClient __instance, bool __0, out bool __state)
        { __state = __instance.Ready != __0; }
        private static void ReadyPostfix(ScreenStocksRealtimeClient __instance, bool __0, bool __state)
        {
            if (__0 || !__state || Current == null) return;
            Current.Guard(() =>
            {
                if (!ReferenceEquals(Current._realtime, __instance)) return;
                Current._websocketActive = false; Current._candles.Paused = true;
                Current._sourceGeneration = Guid.NewGuid().ToString("N");
                Current._baseline = Current._baselineLayout = null;
                foreach (var connection in Current.Connections()) connection.RawLayoutRevision = null;
                Current.PublishStatus();
            });
        }

        private static void PayoutPrefix(GameManager __instance, RealtimePassivePayoutData __0, out bool __state)
        {
            __state = __0 != null && (bool)AppliedState.GetValue(__instance) && __0.dataEpoch >= 0 &&
                __0.dataEpoch == (long)DataEpoch.GetValue(__instance) && __0.boundary > (long)DividendBoundary.GetValue(__instance);
        }
        private static void PayoutPostfix(RealtimePassivePayoutData __0, bool __state)
        {
            if (!__state || Current == null || _raw == null) return;
            Current.Guard(() =>
            {
                if (JsonObjectParser.ExtractTopLevelString(_raw.Json, "type") == "passivePayout") Current.ConfirmedPayout(__0, _raw.Client);
            });
        }

        private void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                // Never throw bridge errors into the game or silently retain continuity.
                _baseline = _baselineLayout = null; _websocketActive = false; _candles.Reset();
                _lastCaptureError = ex.GetType().Name; _sourceGeneration = Guid.NewGuid().ToString("N"); _replays.Clear();
                foreach (var connection in Connections())
                    if (connection.IsSubscribed || connection.IsCandleSubscribed) connection.Close();
            }
        }

        private void Bind()
        {
            var manager = StockManager.I;
            var source = manager == null ? null : manager.Source as RemoteMarketDataSource;
            var realtime = source == null ? null : RealtimeField.GetValue(source) as ScreenStocksRealtimeClient;
            if (ReferenceEquals(source, _source) && ReferenceEquals(realtime, _realtime)) return;
            if (_realtime != null) _realtime.OnPlayerDataReset -= ResetPlayerSession;
            _source = source; _realtime = realtime;
            if (_realtime != null) _realtime.OnPlayerDataReset += ResetPlayerSession;
            ResetStreams();
        }

        private void ResetPlayerSession(long epoch) { Guard(ResetStreams); }
        private void ResetStreams()
        {
            _sourceGeneration = Guid.NewGuid().ToString("N");
            _baseline = _baselineLayout = null; _websocketActive = false;
            _legacyPrices = false;
            _payoutEpoch = _payoutBoundary = long.MinValue;
            _candles.Reset(); _lastMaterial = _lastStatus = string.Empty;
            _replays.Clear();
            foreach (var connection in Connections()) connection.RawLayoutRevision = null;
        }

        internal void Accept(ScreenStocksRealtimeClient client, string raw, string? layout, IReadOnlyList<string> ids,
            IReadOnlyList<float> prices, long timeMs, long tick)
        {
            Bind();
            if (!ReferenceEquals(client, _realtime) || ids.Count != prices.Count) return;
            var type = JsonObjectParser.ExtractTopLevelString(raw, "type");
            if (type != "prices" && type != "priceVector" && type != "priceVectorBaseline") return;
            _legacyPrices = type == "prices";
            if (type == "priceVectorBaseline") { _baseline = raw; _baselineLayout = layout; }
            _websocketActive = true; _candles.Paused = false;
            PublishStatus();
            if (_instant)
                foreach (var connection in Connections())
                {
                    if (!connection.IsSubscribed) continue;
                    if (type == "prices") SendRaw(connection, raw, false);
                    else if (type == "priceVectorBaseline")
                    { SendRaw(connection, raw, false); connection.RawLayoutRevision = layout; }
                    else if (connection.RawLayoutRevision == layout && _baseline != null) SendRaw(connection, raw, false);
                    else if (_baseline != null && _baselineLayout == layout)
                    { Bootstrap(connection); SendRaw(connection, raw, false); }
                }
            if (timeMs <= 0 || tick < 0) return;
            if (ServerClock.IsSynchronized) _candles.Advance(ServerClock.NowMs);
            if (timeMs <= long.MaxValue - 1000) _candles.Advance(timeMs);
            var late = _candles.LateObservations;
            for (var i = 0; i < ids.Count; i++) _candles.Add(ids[i], prices[i], timeMs, tick);
            if (_candles.LateObservations != late)
                PublishToCandles("market.candle_late", "{\"generation\":" + Q(_candles.Generation) +
                    ",\"sourceTimeMs\":" + timeMs + ",\"tick\":" + tick + ",\"lateObservations\":" + _candles.LateObservations + "}");
        }

        private void ConfirmedPayout(RealtimePassivePayoutData payout, ScreenStocksRealtimeClient client)
        {
            Bind();
            if (!ReferenceEquals(client, _realtime) || payout.dataEpoch < _payoutEpoch ||
                (payout.dataEpoch == _payoutEpoch && payout.boundary <= _payoutBoundary)) return;
            _payoutEpoch = payout.dataEpoch; _payoutBoundary = payout.boundary;
            long? boundaryMs = null;
            ReadSchedule(out var epoch, out var interval, out _);
            try { if (interval > 0 && payout.boundary >= 0) boundaryMs = checked(epoch + checked(payout.boundary * interval)); }
            catch (OverflowException) { }
            PublishState("market.dividend_paid", "{\"dataEpoch\":" + payout.dataEpoch + ",\"boundaryIndex\":" + payout.boundary +
                ",\"scheduledBoundaryMs\":" + (boundaryMs?.ToString(CultureInfo.InvariantCulture) ?? "null") +
                ",\"dividendAmount\":" + Q(StateService.FormatBigNumber(payout.dividendAmount.ToBigNumber())) +
                ",\"observedServerTimeMs\":" + (ServerClock.IsSynchronized ? ServerClock.NowMs.ToString(CultureInfo.InvariantCulture) : "null") + "}");
        }

        internal void Update()
        {
            Guard(() =>
            {
                Bind();
                if (_realtime == null || !_realtime.Ready) { _websocketActive = false; _candles.Paused = true; }
                PublishStatus();
                if (ServerClock.IsSynchronized) _candles.Advance(ServerClock.NowMs);
                DrainReplays();
                var now = _timer.Elapsed.TotalSeconds;
                if (now >= _nextClock)
                { _nextClock = now + 1; PublishState("market.clock", ReadClockJson()); }
                if (!_cadence.Due(now) || !Connections().Any(connection => connection.IsSubscribed && !connection.IsClosed)) return;
                var snapshot = _state.CreateSnapshot();
                ReadSchedule(out var epoch, out var interval, out var tickMs);
                var material = MarketClock.MaterialSnapshot(snapshot) + "/" + epoch + "/" + interval + "/" + tickMs + "/" + StatusJson();
                if (material != _lastMaterial)
                { _lastMaterial = material; PublishState("market.updated", BridgeJson.SerializeSnapshot(snapshot)); }
            });
        }

        internal void SubscribeState(BridgeConnection connection)
        {
            Bind();
            connection.IsSubscribed = true;
            connection.SendStream("market.updated", _state.SnapshotJson());
            connection.SendStream("market.clock", ReadClockJson());
            connection.SendStream("market.feed_status", StatusJson());
            if (_instant) Bootstrap(connection);
        }

        private void Bootstrap(BridgeConnection connection)
        {
            if (_baseline == null) return;
            SendRaw(connection, _baseline, true);
            connection.RawLayoutRevision = _baselineLayout;
        }
        private void SendRaw(BridgeConnection connection, string raw, bool replayed) => connection.SendStream("market.price", "{\"sourceGeneration\":" + Q(_sourceGeneration) +
            ",\"replayedBaseline\":" + (replayed ? "true" : "false") + ",\"sourceJson\":" + Q(raw) + "}");

        internal void Handle(BridgeRequest request, BridgeConnection connection)
        {
            if (connection.IsClosed) return;
            var args = request.@params;
            try
            {
                Bind();
                string result;
                switch (request.method)
                {
                    case "market.set_rate": _cadence.SetRate(args?.rateHz ?? 0); _lastStatus = string.Empty; PublishStatus(); result = ControlsJson(); break;
                    case "market.set_instant":
                        if (!Available) throw new InvalidOperationException("Raw market hooks are unavailable.");
                        var enabled = args?.enabled ?? false;
                        if (enabled && !_instant)
                            foreach (var target in Connections())
                                if (target.IsSubscribed) { target.SendStream("market.updated", _state.SnapshotJson()); Bootstrap(target); }
                        _instant = enabled; _lastStatus = string.Empty; PublishStatus(); result = ControlsJson(); break;
                    case "market.stream_status": result = ControlsJson(); break;
                    case "market.candles.subscribe":
                        if (!Available) throw new InvalidOperationException("Canonical candle hooks are unavailable.");
                        var replay = _candles.Replay(args?.generation ?? string.Empty, args?.afterSequence ?? 0);
                        connection.IsCandleSubscribed = true;
                        result = "{\"generation\":" + Q(_candles.Generation) + ",\"latestSequence\":" + _candles.Sequence +
                            ",\"gap\":" + (replay.Gap ? "true" : "false") + ",\"replayCount\":" + replay.Updates.Count + "}";
                        connection.Send(ProtocolJson.Response(request.id, true, result, string.Empty), false);
                        connection.SendStream("market.feed_status", StatusJson());
                        _replays[connection] = new Queue<CandleUpdate>(replay.Updates);
                        return;
                    case "market.candles.unsubscribe": connection.IsCandleSubscribed = false; _replays.Remove(connection); result = "{\"subscribed\":false}"; break;
                    default: connection.Send(ProtocolJson.Error(request.id, "unknown_method", "Unknown market stream method."), false); return;
                }
                connection.Send(ProtocolJson.Response(request.id, true, result, string.Empty), false);
            }
            catch (ArgumentOutOfRangeException ex) { connection.Send(ProtocolJson.Error(request.id, "invalid_params", ex.Message), false); }
            catch (Exception ex) { connection.Send(ProtocolJson.Error(request.id, "market_stream_unavailable", ex.Message), false); }
        }

        private string ControlsJson() => "{\"snapshotRateHz\":" + _cadence.RateHz.ToString("R", CultureInfo.InvariantCulture) +
            ",\"instant\":" + (_instant ? "true" : "false") + ",\"hooksAvailable\":" + (Available ? "true" : "false") + ",\"feed\":" + StatusJson() + "}";
        private string StatusJson() => "{\"sourceGeneration\":" + Q(_sourceGeneration) + ",\"candleGeneration\":" + Q(_candles.Generation) +
            ",\"lastCaptureError\":" + (_lastCaptureError == null ? "null" : Q(_lastCaptureError)) +
            ",\"snapshotRateHz\":" + _cadence.RateHz.ToString("R", CultureInfo.InvariantCulture) + ",\"instant\":" + (_instant ? "true" : "false") +
            ",\"source\":" + Q(_websocketActive ? "websocket" : _source?.IsReady == true ? "http_fallback" : "unavailable") +
            ",\"waitingForBaseline\":" + (_instant && !_legacyPrices && _baseline == null ? "true" : "false") + "}";
        private void PublishStatus()
        {
            var json = StatusJson();
            if (json == _lastStatus) return;
            _lastStatus = json; PublishState("market.feed_status", json); PublishToCandles("market.feed_status", json, true);
        }
        private void DrainReplays()
        {
            foreach (var connection in new List<BridgeConnection>(_replays.Keys))
            {
                var pending = _replays[connection];
                if (pending.Any(update => !_candles.IsRetained(update)))
                {
                    connection.SendStream("market.candle_gap", "{\"generation\":" + Q(_candles.Generation) +
                        ",\"latestSequence\":" + _candles.Sequence + ",\"reason\":\"replay_expired\",\"resubscribeRequired\":true}");
                    connection.IsCandleSubscribed = false;
                    _replays.Remove(connection); continue;
                }
                if (_candles.Paused && !connection.IsClosed && connection.IsCandleSubscribed) continue;
                if (!connection.IsClosed && connection.IsCandleSubscribed)
                    for (var i = 0; i < 8 && pending.Count > 0; i++) connection.SendStream("market.candle", pending.Dequeue().Json);
                if (connection.IsClosed || !connection.IsCandleSubscribed || pending.Count == 0) _replays.Remove(connection);
            }
        }
        private BridgeConnection[] Connections() => _server()?.Connections ?? Array.Empty<BridgeConnection>();
        private void PublishState(string name, string data)
        { foreach (var connection in Connections()) if (connection.IsSubscribed) connection.SendStream(name, data); }
        private void PublishToCandles(string name, string data, bool skipState = false)
        { foreach (var connection in Connections()) if (connection.IsCandleSubscribed && (!skipState || !connection.IsSubscribed)) connection.SendStream(name, data); }

        internal static string ReadClockJson()
        {
            ReadSchedule(out var epoch, out var interval, out var tickMs);
            var manager = StockManager.I;
            return MarketClock.Json(ServerClock.IsSynchronized, ServerClock.NowMs,
                manager == null ? 0 : manager.Source?.ServerTick ?? 0, tickMs, epoch, interval);
        }
        private static void ReadSchedule(out long epoch, out long interval, out int tickMs)
        {
            var game = GameManager.I;
            var settings = game == null ? null : game.Settings;
            epoch = settings == null ? 0 : settings.MarketEpochUnixMilliseconds;
            var seconds = settings == null ? 0 : settings.globalDividendIntervalSeconds;
            interval = seconds > 0 && !float.IsInfinity(seconds) && seconds < long.MaxValue / 1000d ? (long)Math.Round(seconds * 1000d) : 0;
            tickMs = settings == null ? 0 : settings.tickRateMs;
        }
        private static string Q(string value) => BridgeJson.Quote(value);
        public void Dispose()
        {
            if (_realtime != null) _realtime.OnPlayerDataReset -= ResetPlayerSession;
            _harmony?.UnpatchSelf(); _harmony = null;
            if (ReferenceEquals(Current, this)) Current = null;
        }
        private sealed class RawContext
        {
            internal readonly ScreenStocksRealtimeClient Client; internal readonly string Json;
            internal RawContext(ScreenStocksRealtimeClient client, string json) { Client = client; Json = json; }
        }
    }
}
