using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal sealed class ActivityModule : IDisposable
    {
        private static readonly FieldInfo ClientField = AccessTools.Field(typeof(StockManager), "_humanActivityClient");
        private static readonly FieldInfo FocusField = AccessTools.Field(typeof(StockManager), "_chartActivityFocus");
        private static readonly FieldInfo BucketsField = AccessTools.Field(typeof(HumanActivityClient), "_buckets");
        private static readonly FieldInfo SizeField = AccessTools.Field(typeof(HumanActivityClient), "_ticksPerBucket");
        private static readonly FieldInfo GoodField = AccessTools.Field(typeof(HumanActivityClient), "_hasGoodData");
        private static readonly FieldInfo FocusedField = AccessTools.Field(typeof(HumanActivityClient), "_focusedStockId");
        private static readonly FieldInfo DisposedField = AccessTools.Field(typeof(HumanActivityClient), "_disposed");
        private static readonly FieldInfo RealtimeField = AccessTools.Field(typeof(HumanActivityClient), "_realtime");
        private static readonly MethodInfo Placement = AccessTools.Method(typeof(HumanActivityAligner), "FindPlacementSample");
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly ActivityFeed _feed;
        private Harmony? _harmony;
        private HumanActivityClient? _client;
        private StockManager? _manager;
        private BridgeConnection? _ownerConnection;
        private readonly Dictionary<string, BridgeConnection> _subscribers = new Dictionary<string, BridgeConnection>();
        private bool _bridgeFocus;
        private string? _lastResponseUtc, _lastValueChangeUtc;
        private double? _lastResponseAt, _lastValueChangeAt;
        private long? _oldestRetainedBucketId, _lastServerTick;
        private string _valueSignature = string.Empty;
        private string? _fault;
        private double _nextRefresh;
        private bool _captureDirty = true;
        private string? _capturedStock;
        private string? _capturedGeneration;
        private bool _capturedGood;
        private int _capturedSize;
        private long[]? _capturedTicks;
        private Dictionary<string, string>? _capturedRows;
        private static ActivityModule? Current;
        internal string ActiveStockId => _feed.Owner == null ? string.Empty : _feed.EffectiveStock;
        internal bool Available => _harmony != null;

        internal ActivityModule()
        {
            _feed = new ActivityFeed(() => _clock.Elapsed.TotalSeconds, SetNativeFocus);
            _feed.Changed += frames =>
            {
                foreach (var connection in _subscribers.Values.ToArray())
                    foreach (var frame in frames) connection.SendActivity(frame);
            };
        }

        internal void Install()
        {
            foreach (var member in new object[] { ClientField, FocusField, BucketsField, SizeField, GoodField,
                FocusedField, DisposedField, RealtimeField, Placement })
                if (member == null) throw new InvalidOperationException("Required native activity member is missing.");
            Current = this;
            _harmony = new Harmony("screenstocks.bridge.activity.v2");
            try
            {
                Patch(typeof(StockManager), "SetChartActivityFocus", nameof(FocusPrefix), null);
                Patch(typeof(HumanActivityClient), "Apply", null, nameof(FullPostfix));
                Patch(typeof(HumanActivityClient), "HandleRealtimeActivityDelta", nameof(DeltaPrefix), nameof(DeltaPostfix));
                Patch(typeof(HumanActivityClient), "ClearFocusedData", null, nameof(ClearPostfix));
                Patch(typeof(HumanActivityClient), "HandleRealtimeConnectionChanged", null, nameof(ConnectionPostfix));
                Refresh();
            }
            catch { Dispose(); throw; }
        }

        private void Patch(Type type, string name, string? prefix, string? postfix)
        {
            var target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
            _harmony!.Patch(target, prefix == null ? null : new HarmonyMethod(typeof(ActivityModule), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(ActivityModule), postfix));
        }

        private static void FocusPrefix(ref string __0)
        {
            var module = Current;
            if (module == null || module._bridgeFocus) return;
            __0 = module._feed.RouteGraphFocus(__0);
        }

        private static void FullPostfix(HumanActivityClient __instance, HumanActivityResponse __0)
        { Current?.Observe(__instance, __0.lastTick, null); }

        private static void DeltaPrefix(HumanActivityClient __instance, HumanActivityDelta __0, out bool __state)
        {
            __state = __0 != null && __0.buckets != null && __0.ticksPerBucket > 0 &&
                !(bool)DisposedField.GetValue(__instance) && (bool)GoodField.GetValue(__instance) &&
                __0.ticksPerBucket == (int)SizeField.GetValue(__instance) &&
                string.Equals(__0.stockId, (string?)FocusedField.GetValue(__instance), StringComparison.Ordinal);
        }

        private static void DeltaPostfix(HumanActivityClient __instance, HumanActivityDelta __0, bool __state)
        { if (__state) Current?.Observe(__instance, __0.lastTick, __0.oldestRetainedBucketId); }

        private static void ClearPostfix(HumanActivityClient __instance)
        {
            var module = Current;
            if (module == null || !ReferenceEquals(module._client, __instance)) return;
            module.ClearSource();
        }

        private static void ConnectionPostfix(HumanActivityClient __instance)
        {
            var module = Current;
            if (module == null || !ReferenceEquals(module._client, __instance)) return;
            // A transport transition cannot inherit source-receipt evidence from
            // the previous connection. Retained native rows remain descriptive.
            module.ClearSource();
        }

        private void Observe(HumanActivityClient client, long lastTick, long? oldest)
        {
            try
            {
                // Hooks on retired clients must never overwrite the current generation.
                var manager = StockManager.I;
                if (manager == null || !ReferenceEquals(ClientField.GetValue(manager), client)) return;
                if (!ReferenceEquals(_client, client)) Refresh();
                _lastResponseUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                _lastResponseAt = _clock.Elapsed.TotalSeconds;
                _lastServerTick = lastTick; _oldestRetainedBucketId = oldest;
                _captureDirty = true;
                // Capture every accepted mutation, even if several arrive between frames.
                if (!_bridgeFocus) Refresh();
            }
            catch (Exception ex) { _fault = ex.Message; }
        }

        private void ClearSource()
        {
            _lastResponseUtc = _lastValueChangeUtc = null;
            _lastResponseAt = _lastValueChangeAt = null;
            _oldestRetainedBucketId = _lastServerTick = null;
            _valueSignature = string.Empty;
            _captureDirty = true; _capturedRows = null; _capturedTicks = null;
            _feed.Reset();
        }

        private void SetNativeFocus(string stock)
        {
            var manager = StockManager.I;
            if (manager == null) throw new ActivityError("not_ready", "The game's stock manager is unavailable.");
            var previousFocus = (string?)FocusField.GetValue(manager) ?? string.Empty;
            _bridgeFocus = true;
            try { manager.SetChartActivityFocus(stock); }
            catch
            {
                try { manager.SetChartActivityFocus(previousFocus); }
                catch (Exception ex) { _fault = "Could not restore focus after a native failure: " + ex.Message; }
                throw;
            }
            finally { _bridgeFocus = false; }
        }

        internal void Update()
        {
            foreach (var pair in _subscribers.ToArray())
                if (pair.Value.IsClosed) _subscribers.Remove(pair.Key);
            if (_ownerConnection != null && _ownerConnection.IsClosed)
            {
                try { _feed.Disconnect(_ownerConnection.SessionId); }
                catch (Exception ex) { _fault = "Could not restore graph focus: " + ex.Message; }
                _ownerConnection = null;
            }
            if (_fault != null) { InvalidateSubscribers(); return; }
            if (_clock.Elapsed.TotalSeconds < _nextRefresh) return;
            _nextRefresh = _clock.Elapsed.TotalSeconds + 0.25;
            try { Refresh(); }
            catch (Exception ex) { _fault = ex.Message; InvalidateSubscribers(); }
        }

        private void InvalidateSubscribers()
        {
            foreach (var connection in _subscribers.Values) connection.Close();
            _subscribers.Clear();
        }

        private void Refresh()
        {
            var manager = StockManager.I;
            var client = manager == null ? null : ClientField.GetValue(manager) as HumanActivityClient;
            if (!ReferenceEquals(_manager, manager) || !ReferenceEquals(_client, client))
            {
                if (_ownerConnection != null)
                {
                    try { _feed.Disconnect(_ownerConnection.SessionId); } catch (ActivityError) { }
                    _ownerConnection = null;
                }
                _manager = manager; _client = client;
                _feed.SetInitialGraph(manager == null ? string.Empty : (string?)FocusField.GetValue(manager) ?? string.Empty);
                ClearSource();
            }
            var stock = _feed.EffectiveStock;
            var ready = manager != null && client != null && !(bool)DisposedField.GetValue(client) &&
                manager.Source is RemoteMarketDataSource && manager.Source.IsReady;
            var good = ready && (bool)GoodField.GetValue(client);
            var ticks = ready ? ((RemoteMarketDataSource)manager!.Source).GetHistorySampleTicks(stock) : null;
            var size = good ? (int)SizeField.GetValue(client) : 0;
            var rowsUnchanged = !_captureDirty && _capturedRows != null &&
                _capturedGeneration == _feed.Generation && stock == _capturedStock &&
                good == _capturedGood && size == _capturedSize && SameTicks(ticks);
            var rows = _capturedRows;
            var signature = _valueSignature;
            if (!rowsUnchanged)
            {
                rows = CaptureNative(client, stock, ticks, out signature);
                _capturedRows = rows; _capturedStock = stock; _capturedGood = good; _capturedSize = size;
                _capturedGeneration = _feed.Generation;
                if (ticks == null) _capturedTicks = null;
                else
                {
                    if (_capturedTicks == null || _capturedTicks.Length != ticks.Count) _capturedTicks = new long[ticks.Count];
                    for (var i = 0; i < ticks.Count; i++) _capturedTicks[i] = ticks[i];
                }
                _captureDirty = false;
            }
            if (_lastResponseAt.HasValue && signature != _valueSignature)
            {
                _lastValueChangeUtc = _lastResponseUtc; _lastValueChangeAt = _lastResponseAt;
            }
            _valueSignature = signature;
            var realtime = client == null ? null : RealtimeField.GetValue(client) as ScreenStocksRealtimeClient;
            var state = "{\"stockId\":" + Q(stock) + ",\"focusMode\":" + Q(_feed.Mode) + ",\"ownerSessionId\":" + Q(_feed.Owner) +
                ",\"ready\":" + Bool(ready) + ",\"hasAcceptedData\":" + Bool(good) +
                ",\"connectionState\":" + Q(realtime == null ? "unknown" : realtime.Ready ? "ready" : "not_ready") +
                ",\"ticksPerBucket\":" + (size > 0 ? size.ToString(CultureInfo.InvariantCulture) : "null") +
                ",\"fullScaleImpact\":" + (good ? Number(client!.FullScaleImpactFraction) : "null") +
                ",\"lastResponseAtUtc\":" + Q(_lastResponseUtc) + ",\"lastValueChangeAtUtc\":" + Q(_lastValueChangeUtc) +
                ",\"lastServerTick\":" + NullableNumber(_lastServerTick) +
                ",\"oldestRetainedBucketId\":" + NullableNumber(_oldestRetainedBucketId) +
                ",\"retentionBoundsKnown\":" + Bool(_oldestRetainedBucketId.HasValue && _lastServerTick.HasValue) +
                ",\"coverageComplete\":null}";
            _feed.Publish(state, rows!, rowsUnchanged);
        }

        private bool SameTicks(IReadOnlyList<long>? ticks)
        {
            if (ticks == null) return _capturedTicks == null;
            if (_capturedTicks == null || ticks.Count != _capturedTicks.Length) return false;
            for (var i = 0; i < ticks.Count; i++) if (ticks[i] != _capturedTicks[i]) return false;
            return true;
        }

        internal static Dictionary<string, string> CaptureNative(HumanActivityClient? client, string stock,
            IReadOnlyList<long>? ticks, out string signature)
        {
            var good = client != null && (bool)GoodField.GetValue(client);
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            var size = good ? (int)SizeField.GetValue(client) : 0;
            var samples = client != null && ticks != null ? client.GetAligned(stock, ticks) : null;
            var buckets = good ? new List<HumanActivityBucket>((List<HumanActivityBucket>)BucketsField.GetValue(client)) : new List<HumanActivityBucket>();
            buckets.Sort((a, b) => a.BucketId.CompareTo(b.BucketId));
            long? priorId = null;
            var occurrence = 0;
            foreach (var bucket in buckets)
            {
                var duplicate = priorId == bucket.BucketId;
                occurrence = duplicate ? occurrence + 1 : 0; priorId = bucket.BucketId;
                long? placed = null;
                if (!duplicate && ticks != null && ticks.Count > 0 && size > 0)
                {
                    try
                    {
                        var start = checked(bucket.BucketId * size);
                        var end = checked(start + size - 1);
                        if (end >= ticks[0] && end <= ticks[ticks.Count - 1])
                        {
                            var index = (int)Placement.Invoke(null, new object[] { ticks, start, end });
                            if (index >= 0 && index < ticks.Count) placed = ticks[index];
                        }
                    }
                    catch (OverflowException) { }
                }
                rows["bucket:" + bucket.BucketId + ":" + occurrence] = "{\"kind\":\"bucket\",\"bucketId\":" + bucket.BucketId +
                    ",\"up\":" + bucket.Up + ",\"down\":" + bucket.Down + ",\"upImpact\":" + Number(bucket.UpImpact) +
                    ",\"downImpact\":" + Number(bucket.DownImpact) + ",\"placementSampleTick\":" + NullableNumber(placed) + "}";
            }
            if (ticks != null && samples != null)
                for (var i = 0; i < Math.Min(ticks.Count, samples.Count); i++)
                {
                    var value = samples[i];
                    rows["sample:" + ticks[i]] = "{\"kind\":\"sample\",\"sampleTick\":" + ticks[i] +
                        ",\"up\":" + value.Up + ",\"down\":" + value.Down + ",\"total\":" + value.Total + ",\"net\":" + value.Net +
                        ",\"upImpact\":" + Number(value.UpImpact) + ",\"downImpact\":" + Number(value.DownImpact) +
                        ",\"totalImpact\":" + Number(value.TotalImpact) + ",\"netImpact\":" + Number(value.NetImpact) + "}";
                }
            signature = string.Join("|", buckets.Select(b => b.BucketId + ":" + b.Up + ":" + b.Down + ":" + Number(b.UpImpact) + ":" + Number(b.DownImpact))) + "/" + size + "/" + (good ? Number(client!.FullScaleImpactFraction) : "null");
            return rows;
        }

        internal void Handle(BridgeRequest request, BridgeConnection connection)
        {
            try
            {
                if (connection.IsClosed) return;
                if (request.@params?.schemaVersion != 2)
                    throw new ActivityError("unsupported_activity_version", "Use schemaVersion=2 and the activity.* interface; upgrade the Python client.");
                if (_harmony == null || _fault != null) throw new ActivityError("activity_unavailable", _fault ?? "Native activity hooks are unavailable.");
                Refresh();
                var args = request.@params!;
                string result;
                switch (request.method)
                {
                    case "activity.pin":
                    case "activity.takeover":
                        if (connection.IsClosed) return;
                        var game = GameManager.I;
                        if (!RequestValidation.IsValidStockId(args.stockId)) throw new ActivityError("invalid_stock", "Specify a valid stockId.");
                        if (game == null || game.Data == null || _manager?.Source?.IsReady != true) throw new ActivityError("not_ready", "The market is not ready.");
                        if (!game.IsStockVisibleToPlayer(args.stockId)) throw new ActivityError("stock_unavailable", "Stock is not visible.");
                        _feed.Pin(connection.SessionId, args.stockId, request.method == "activity.takeover");
                        _ownerConnection = connection;
                        Refresh();
                        result = Snapshot(connection, string.Empty, 0);
                        break;
                    case "activity.unpin":
                        _feed.Unpin(connection.SessionId); _ownerConnection = null;
                        Refresh(); result = Snapshot(connection, string.Empty, 0); break;
                    case "activity.snapshot":
                        result = Snapshot(connection, args.snapshotId, args.offset); break;
                    case "activity.subscribe":
                        result = Snapshot(connection, string.Empty, 0);
                        connection.IsActivitySubscribed = true;
                        _subscribers[connection.SessionId] = connection;
                        break;
                    case "activity.unsubscribe":
                        _subscribers.Remove(connection.SessionId); connection.IsActivitySubscribed = false;
                        result = "{\"schemaVersion\":2,\"subscribed\":false}"; break;
                    default: throw new ActivityError("unknown_method", "Unknown activity method.");
                }
                connection.Send(ProtocolJson.Response(request.id, true, result, string.Empty), false);
            }
            catch (ActivityError ex) { connection.Send(ProtocolJson.Error(request.id, ex.Code, ex.Message, ex.RetryAfterMs), false); }
            catch (Exception ex)
            {
                _fault = ex.Message;
                InvalidateSubscribers();
                connection.Send(ProtocolJson.Error(request.id, "activity_unavailable", "Native activity could not be read safely."), false);
            }
        }

        private string Snapshot(BridgeConnection connection, string id, int offset)
        {
            // Freeze observation evidence with the same immutable snapshot as rows.
            return _feed.Snapshot(connection.SessionId, id, offset,
                ",\"observedAtUtc\":" + Q(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) +
                ",\"responseAgeSeconds\":" + Age(_lastResponseAt) + ",\"valueChangeAgeSeconds\":" + Age(_lastValueChangeAt));
        }

        private string Age(double? at) => at.HasValue ? Number(Math.Max(0, _clock.Elapsed.TotalSeconds - at.Value)) : "null";
        private static string Q(string? value) => value == null ? "null" : BridgeJson.Quote(value);
        private static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        private static string NullableNumber(long? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";
        private static string Bool(bool value) => value ? "true" : "false";

        public void Dispose()
        {
            try { if (_feed.Owner != null) _feed.Disconnect(_feed.Owner); }
            catch (Exception) { /* Native scene may already be destroyed; ownership still ends. */ }
            finally { _harmony?.UnpatchSelf(); _harmony = null; if (ReferenceEquals(Current, this)) Current = null; }
        }
    }
}
