using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ScreenStocks.Net;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class TradeRecoveryService : IDisposable
    {
        internal static TradeRecoveryService? Current { get; private set; }
        internal readonly TradeRecoveryController Controller = new TradeRecoveryController();
        internal static bool BlocksCommands => Current?.Controller.BlocksCommands ?? false;
        internal static string ErrorCode => Current?.Controller.Phase == TradeRecoveryPhase.Failed ? "trade_recovery_failed" : "trade_recovery_in_progress";
        internal static string ErrorMessage => Current?.Controller.Phase == TradeRecoveryPhase.Failed
            ? "Trade recovery failed. Restart the game before submitting more commands."
            : "Trade session recovery is in progress. No new command was submitted.";
        private readonly Action<string>? _log;
        private TradeRecoveryPhase _previous;
        private int _completed;
        private double _showSuccessUntil;

        internal TradeRecoveryService(Action<string>? log = null) { _log = log; Current = this; }

        internal void Update()
        {
            var now = (double)Time.realtimeSinceStartup;
            var broker = TradeRecoveryState.BrokerFor(StockManager.I);
            var command = broker == null ? null : NativeRecoveryAttempt.Field(broker, "_ambiguousCommand");
            Controller.Tick(now, command, () => new NativeRecoveryAttempt(command!));
            if (_previous != Controller.Phase)
            {
                _log?.Invoke("Trade recovery: " + Controller.Phase +
                    (Controller.Phase == TradeRecoveryPhase.Failed ? ": " + Controller.Error : string.Empty));
                _previous = Controller.Phase;
            }
            if (_completed != Controller.CompletedCount)
            {
                _completed = Controller.CompletedCount;
                _showSuccessUntil = now + 8;
                _log?.Invoke("Trade connection recovered; original trade outcome remains unknown and authoritative state was refreshed.");
            }
        }

        internal static string Snapshot()
        {
            var current = Current;
            if (current == null) return "{\"available\":false,\"phase\":\"unavailable\",\"blocksCommands\":false}";
            return "{\"available\":true,\"phase\":" + BridgeJson.Quote(current.Controller.Phase.ToString().ToLowerInvariant()) +
                ",\"blocksCommands\":" + (BlocksCommands ? "true" : "false") +
                ",\"secondsUntilRecovery\":" + (current.Controller.Phase == TradeRecoveryPhase.Observing
                    ? current.Controller.SecondsUntilRecovery(Time.realtimeSinceStartup) : 0).ToString("0.0", CultureInfo.InvariantCulture) +
                ",\"completedRecoveries\":" + current.Controller.CompletedCount.ToString(CultureInfo.InvariantCulture) +
                ",\"error\":" + BridgeJson.Quote(current.Controller.Error) + "}";
        }

        internal void DrawStatus()
        {
            string text;
            switch (Controller.Phase)
            {
                case TradeRecoveryPhase.Observing:
                    text = "Trade result pending. Connection recovery in " +
                        Math.Ceiling(Controller.SecondsUntilRecovery(Time.realtimeSinceStartup)).ToString(CultureInfo.InvariantCulture) + " seconds.";
                    break;
                case TradeRecoveryPhase.Draining:
                case TradeRecoveryPhase.Reloading:
                case TradeRecoveryPhase.Reconnecting:
                    text = "Recovering trade connection… Trading is paused while server state reloads.";
                    break;
                case TradeRecoveryPhase.Failed:
                    text = "Trade recovery failed. Restart the game to resume trading.";
                    break;
                default:
                    if (Time.realtimeSinceStartup >= _showSuccessUntil) return;
                    text = "Trade connection recovered. Server state refreshed; the original trade outcome is unknown.";
                    break;
            }
            var style = new GUIStyle(GUI.skin.box) { wordWrap = true, fontSize = 16, alignment = TextAnchor.MiddleCenter };
            GUI.Box(new Rect(20, 20, Mathf.Max(80, Mathf.Min(620, Screen.width - 40)), 84), text, style);
        }

        public void Dispose() { if (ReferenceEquals(Current, this)) Current = null; }
    }

    internal sealed class NativeRecoveryAttempt : ITradeRecoveryAttempt
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly ScreenStocksAuthManager _auth;
        private readonly StockManager _stocks;
        private readonly RemoteTradeBroker _broker;
        private readonly ScreenStocksApiClient _api;
        private readonly PlayerServerStateClient _state;
        private readonly string _principal;
        private readonly Guid _requestId;
        private readonly long _baseEpoch;
        private long _readSequence;
        private bool _reloading;

        internal static object? Field(object instance, string name) => instance.GetType().GetField(name, Flags)!.GetValue(instance);
        private static void Invoke(object instance, string name) => instance.GetType().GetMethod(name, Flags)!.Invoke(instance, null);

        internal static void ValidateSurface()
        {
            foreach (var pair in new[] {
                (typeof(StockManager), "_queuedManualActions"),
                (typeof(RemoteTradeBroker), "_ambiguousCommand"),
                (typeof(RemoteTradeBroker), "_tradeDispatchGate"),
                (typeof(ScreenStocksApiClient), "_durableCommandGate"),
                (typeof(ScreenStocksApiClient), "_durableCommandSessionGeneration"),
                (typeof(PlayerServerStateClient), "_fetchGate"),
                (typeof(RemoteUpgradesClient), "_purchaseGate"),
                (typeof(RemoteLevelsClient), "_claimGate") })
                if (pair.Item1.GetField(pair.Item2, Flags) == null) throw new MissingFieldException(pair.Item1.FullName, pair.Item2);
            foreach (var type in new[] { typeof(RemoteTradeBroker), typeof(RemoteUpgradesClient), typeof(RemoteLevelsClient) })
                foreach (var name in new[] { "_progressResetBarrierRequested", "_progressResetBarrierHeld" })
                    if (type.GetField(name, Flags)?.FieldType != typeof(bool)) throw new MissingFieldException(type.FullName, name);
            if (typeof(ScreenStocksApiClient).GetMethod("InvalidateDurableCommandSession", Flags) == null)
                throw new MissingMethodException("API command-session invalidation is unavailable.");
            if (typeof(ScreenStocksAuthManager).GetMethod("ResetPendingPlayerCommandsForSessionBoundary", Flags) == null)
                throw new MissingMethodException("Auth command-session boundary is unavailable.");
            var iterator = typeof(ScreenStocksApiClient).GetNestedType("<SendCoroutine>d__69`1", BindingFlags.NonPublic)
                ?? throw new MissingMemberException("The reviewed API coroutine is unavailable.");
            foreach (var name in new[] { "<>4__this", "tcs", "path", "method", "<req>5__3" })
                if (iterator.GetField(name, Flags) == null) throw new MissingFieldException(iterator.FullName, name);
        }

        internal NativeRecoveryAttempt(object command)
        {
            _auth = ScreenStocksAuthManager.I ?? throw new InvalidOperationException("Authentication is not ready.");
            _stocks = StockManager.I ?? throw new InvalidOperationException("Stock manager is not ready.");
            _broker = TradeRecoveryState.BrokerFor(_stocks) ?? throw new InvalidOperationException("Trade broker is not ready.");
            if (!ReferenceEquals(Field(_broker, "_ambiguousCommand"), command)) throw new InvalidOperationException("The pending trade changed.");
            _api = _auth.Api;
            _state = _auth.PlayerState;
            _principal = _auth.Session?.SteamId ?? string.Empty;
            if (string.IsNullOrEmpty(_principal) || !_auth.IsReady || _state == null) throw new InvalidOperationException("The online session is not ready for recovery.");
            _requestId = (Guid)Field(command, "RequestId")!;
            _baseEpoch = _state.Latest?.dataEpoch ?? -1;
        }

        public bool IsCurrent => ReferenceEquals(ScreenStocksAuthManager.I, _auth) && ReferenceEquals(StockManager.I, _stocks) &&
            ReferenceEquals(_auth.Api, _api) && ReferenceEquals(_auth.PlayerState, _state) &&
            ReferenceEquals(TradeRecoveryState.BrokerFor(_stocks), _broker) &&
            _auth.Session != null && _auth.Session.IsAuthenticated && _auth.Session.SteamId == _principal;

        public void Begin()
        {
            TradeRecoveryHooks.MarkBoundary(_requestId);
            // Stop(false) uses the game's normal command-session reset event,
            // invalidating IPO/action generations before request tasks complete.
            var generation = Field(_api, "_durableCommandSessionGeneration");
            _auth.Realtime?.Stop(false);
            if (Equals(generation, Field(_api, "_durableCommandSessionGeneration")))
                Invoke(_auth, "ResetPendingPlayerCommandsForSessionBoundary");
            if (!OwnedApiRequests.Cancel(_api, _principal)) throw new InvalidOperationException("An outstanding request could not be aborted.");
        }

        private static bool GateFree(object owner, string name) => ((SemaphoreSlim)Field(owner, name)!).CurrentCount == 1;
        private static bool ClientDrained(object? owner, string gate)
        {
            if (owner == null) return true;
            return GateFree(owner, gate) && !(bool)Field(owner, "_progressResetBarrierRequested")! &&
                !(bool)Field(owner, "_progressResetBarrierHeld")! && !TradeRecoveryHooks.HasBarrierWaiters(owner);
        }
        public bool Drain()
        {
            if (!_reloading && !OwnedApiRequests.Cancel(_api, _principal)) throw new InvalidOperationException("An outstanding request could not be aborted.");
            return !OwnedApiRequests.HadAbortFailure(_api) && !_broker.HasAmbiguousTrade &&
                GateFree(_api, "_durableCommandGate") && GateFree(_state, "_fetchGate") &&
                ClientDrained(_broker, "_tradeDispatchGate") && ClientDrained(_auth.Upgrades, "_purchaseGate") &&
                ClientDrained(_auth.Levels, "_claimGate") && (int)Field(_stocks, "_queuedManualActions")! == 0;
        }
        public Task Reload()
        {
            _reloading = true;
            _readSequence = OwnedApiRequests.LatestRead(_api)?.Sequence ?? 0;
            return _state.FetchAsync(false);
        }
        public bool HasFreshState
        {
            get
            {
                var read = OwnedApiRequests.LatestRead(_api);
                var latest = _state.LatestAtOrAboveRevisionFloor;
                return read != null && read.Sequence > _readSequence && latest != null &&
                    read.State.steamId == _principal && latest.steamId == _principal &&
                    read.State.dataEpoch >= _baseEpoch && read.State.dataEpoch == latest.dataEpoch &&
                    string.Equals(read.State.playerRevision, latest.playerRevision, StringComparison.Ordinal);
            }
        }
        public void Reconnect()
        {
            if (_auth.Realtime != null) _auth.Realtime.Start(true);
            else typeof(ScreenStocksAuthManager).GetMethod("SetDurableCommandCapability", Flags)!
                .Invoke(_auth, new object[] { DurableCommandCapabilityState.ConfirmedLegacy });
        }
        public bool IsReady => IsCurrent && _auth.IsReady && (_auth.Realtime == null || _auth.Realtime.Ready);
        public void Cancel()
        {
            // Fail closed. Invalidate this session before settling a timed-out
            // reload; a different principal's new requests must remain untouched.
            if (IsCurrent)
            {
                Invoke(_api, "InvalidateDurableCommandSession");
                _broker.ResetPendingCommandForSessionBoundary();
            }
            OwnedApiRequests.Cancel(_api, _principal);
        }
    }
}
