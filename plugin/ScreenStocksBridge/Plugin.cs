using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ScreenStocksBridge
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "screenstocks.bridge";
        public const string PluginName = "Screen Stocks Python Bridge";
        public const string PluginVersion = "0.9.0";

        internal ConfigEntry<int> BridgePort { get; private set; } = null!;
        internal ConfigEntry<string> BridgeToken { get; private set; } = null!;
        internal ConfigEntry<bool> AutoClaimLevelRewards { get; private set; } = null!;
        internal ConfigEntry<bool> AutoCloseOfflineSummary { get; private set; } = null!;
        internal ConfigEntry<int> TransactionHistoryCacheSeconds { get; private set; } = null!;
        internal ConfigEntry<int> TransactionHistoryMinimumRequestIntervalSeconds { get; private set; } = null!;
        private BridgeServer? _server;
        private Harmony? _harmony;
        private Harmony? _transactionHistoryHarmony;
        private Harmony? _newsTickerHarmony;
        private Harmony? _autoActionToastHarmony;
        private readonly StateService _state = new StateService();
        private readonly OfflineProgressCaptureService _offlineProgress = new OfflineProgressCaptureService();
        private readonly NewsTickerService _newsTicker = new NewsTickerService();
        private readonly AutoActionToastService _autoActionToasts = new AutoActionToastService();
        private readonly HumanActivityService _humanActivity = new HumanActivityService();
        private readonly HumanActivityFocusService _humanActivityFocus = new HumanActivityFocusService();
        private readonly AutoActionsService _autoActions = new AutoActionsService();
        private readonly TradeService _trades = new TradeService();
        private readonly UpgradeService _upgrades = new UpgradeService();
        private TransactionHistoryService _transactionHistory = null!;
        private LeaderboardService _leaderboards = null!;
        private float _nextSnapshotAt;
        private float _nextLevelClaimAt;
        private string _lastSnapshot = string.Empty;
        private readonly Dictionary<string, string> _lastHumanActivitySnapshots = new Dictionary<string, string>(StringComparer.Ordinal);

        private void Awake()
        {
            _leaderboards = new LeaderboardService(this);
            TransactionHistoryCacheSeconds = Config.Bind("TransactionHistory", "CacheSeconds", 60,
                "How long transaction-history results stay fresh in memory. Values below 60 are raised to 60; larger values are allowed.");
            TransactionHistoryMinimumRequestIntervalSeconds = Config.Bind("TransactionHistory", "MinimumRequestIntervalSeconds", 30,
                "Minimum gap before the bridge starts a transaction-history request after any observed game request. Values below 30 are raised to 30; larger values are allowed.");
            var normalizedTransactionHistoryConfig = false;
            if (TransactionHistoryCacheSeconds.Value < TransactionHistoryService.MinimumCacheSeconds)
            {
                TransactionHistoryCacheSeconds.Value = TransactionHistoryService.MinimumCacheSeconds;
                normalizedTransactionHistoryConfig = true;
                Logger.LogWarning("TransactionHistory.CacheSeconds cannot be lower than 60; using 60 seconds.");
            }
            if (TransactionHistoryMinimumRequestIntervalSeconds.Value < TransactionHistoryService.MinimumRequestIntervalSeconds)
            {
                TransactionHistoryMinimumRequestIntervalSeconds.Value = TransactionHistoryService.MinimumRequestIntervalSeconds;
                normalizedTransactionHistoryConfig = true;
                Logger.LogWarning("TransactionHistory.MinimumRequestIntervalSeconds cannot be lower than 30; using 30 seconds.");
            }
            if (normalizedTransactionHistoryConfig) Config.Save();
            _transactionHistory = new TransactionHistoryService(this);
            BridgePort = Config.Bind("Bridge", "Port", 48721, "Loopback TCP port used by the Python bridge.");
            BridgeToken = Config.Bind("Bridge", "Token", string.Empty, "Secret required by local Python clients.");
            AutoClaimLevelRewards = Config.Bind("QualityOfLife", "AutoClaimLevelRewards", true,
                "Automatically claim available level rewards through the game's normal level-reward API.");
            AutoCloseOfflineSummary = Config.Bind("QualityOfLife", "AutoCloseOfflineSummary", true,
                "Capture the welcome-back summary for the bridge, then close its screen automatically.");

            if (string.IsNullOrWhiteSpace(BridgeToken.Value))
            {
                BridgeToken.Value = CreateToken();
                Config.Save();
                Logger.LogInfo("Generated Python bridge token: " + BridgeToken.Value);
            }

            Logger.LogInfo("Screen Stocks Python Bridge " + PluginVersion + " initialized.");

            try
            {
                _harmony = new Harmony(PluginGuid + ".offline-progress");
                OfflineProgressHarmonyPatch.CaptureService = _offlineProgress;
                OfflineProgressHarmonyPatch.AutoClose = AutoCloseOfflineSummary.Value;
                OfflineProgressHarmonyPatch.Install(_harmony);
            }
            catch (Exception ex)
            {
                OfflineProgressHarmonyPatch.CaptureService = null;
                OfflineProgressHarmonyPatch.AutoClose = false;
                _harmony?.UnpatchSelf();
                _harmony = null;
                Logger.LogWarning("Could not hook the welcome-back summary: " + ex.Message);
            }

            try
            {
                _transactionHistoryHarmony = new Harmony(PluginGuid + ".transaction-history");
                TransactionHistoryHarmonyPatch.CaptureService = _transactionHistory;
                TransactionHistoryHarmonyPatch.Install(_transactionHistoryHarmony);
            }
            catch (Exception ex)
            {
                TransactionHistoryHarmonyPatch.CaptureService = null;
                _transactionHistoryHarmony?.UnpatchSelf();
                _transactionHistoryHarmony = null;
                Logger.LogWarning("Could not observe the game's Transactions screen requests: " + ex.Message);
            }

            if (BridgePort.Value < 1 || BridgePort.Value > 65535)
            {
                Logger.LogError("Bridge.Port must be between 1 and 65535; listener was not started.");
                return;
            }
            try
            {
                _server = new BridgeServer(BridgePort.Value, BridgeToken.Value);
                _server.Start();
                _newsTicker.SetServer(_server);
                _autoActionToasts.SetServer(_server);
                Logger.LogInfo("Loopback API listening on 127.0.0.1:" + BridgePort.Value + ".");
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not start the loopback API: " + ex.Message);
                _server?.Dispose();
                _server = null;
            }

            if (_server != null)
            {
                try
                {
                    _newsTickerHarmony = new Harmony(PluginGuid + ".news-ticker");
                    NewsTickerHarmonyPatch.CaptureService = _newsTicker;
                    NewsTickerHarmonyPatch.Install(_newsTickerHarmony);
                    _newsTicker.SetAvailable(true);
                }
                catch (Exception ex)
                {
                    NewsTickerHarmonyPatch.CaptureService = null;
                    _newsTickerHarmony?.UnpatchSelf();
                    _newsTickerHarmony = null;
                    _newsTicker.SetAvailable(false);
                    Logger.LogWarning("Could not hook the news ticker: " + ex.Message);
                }

                try
                {
                    _autoActionToastHarmony = new Harmony(PluginGuid + ".auto-action-toasts");
                    AutoActionToastHarmonyPatch.CaptureService = _autoActionToasts;
                    AutoActionToastHarmonyPatch.Install(_autoActionToastHarmony);
                    _autoActionToasts.SetAvailable(true);
                }
                catch (Exception ex)
                {
                    AutoActionToastHarmonyPatch.CaptureService = null;
                    _autoActionToastHarmony?.UnpatchSelf();
                    _autoActionToastHarmony = null;
                    _autoActionToasts.SetAvailable(false);
                    Logger.LogWarning("Could not hook auto-action completion toasts: " + ex.Message);
                }
            }
        }

        private void Update()
        {
            _humanActivityFocus.Update();
            var server = _server;
            server?.Drain(HandleRequestSafely, 32);
            _leaderboards.Update();
            _transactionHistory.Update();
            _trades.Update();
            TryAutoClaimLevelReward();
            if (server == null || (!server.HasMarketSubscribers && !server.HasHumanActivitySubscribers) ||
                Time.unscaledTime < _nextSnapshotAt) return;
            _nextSnapshotAt = Time.unscaledTime + 0.25f;

            if (server.HasMarketSubscribers)
            {
                var snapshotDto = _state.CreateSnapshot();
                if (snapshotDto.ready)
                {
                    var snapshot = BridgeJson.SerializeSnapshot(snapshotDto);
                    if (!string.Equals(snapshot, _lastSnapshot, StringComparison.Ordinal))
                    {
                        _lastSnapshot = snapshot;
                        server.Publish("market.updated", snapshot);
                    }
                }
            }

            var activityStockIds = server.GetSubscribedHumanActivityStockIds();
            var activeStockIds = new HashSet<string>(activityStockIds, StringComparer.Ordinal);
            foreach (var stockId in activityStockIds)
            {
                if (!_humanActivity.TryCreatePage(stockId, 0, 0L, out var page, out _, out _)) continue;
                var activitySnapshot = BridgeJson.SerializeHumanActivityPage(page);
                if (_lastHumanActivitySnapshots.TryGetValue(stockId, out var previous) &&
                    string.Equals(activitySnapshot, previous, StringComparison.Ordinal)) continue;
                _lastHumanActivitySnapshots[stockId] = activitySnapshot;
                server.PublishHumanActivity(stockId, activitySnapshot);
            }

            var staleStockIds = new List<string>();
            foreach (var stockId in _lastHumanActivitySnapshots.Keys)
                if (!activeStockIds.Contains(stockId)) staleStockIds.Add(stockId);
            foreach (var stockId in staleStockIds) _lastHumanActivitySnapshots.Remove(stockId);
        }

        internal void LogWarning(string message) => Logger.LogWarning(message);

        private void TryAutoClaimLevelReward()
        {
            if (!AutoClaimLevelRewards.Value || Time.unscaledTime < _nextLevelClaimAt) return;
            _nextLevelClaimAt = Time.unscaledTime + 1f;

            var game = GameManager.I;
            if (game == null || game.Data == null || StockManager.I == null || !game.HasClaimableLevelReward()) return;

            var entryCount = game.GetLevelEntryCount();
            for (var index = 0; index < entryCount; index++)
            {
                if (!game.CanClaimLevel(index)) continue;

                try
                {
                    if (game.ClaimLevel(index))
                        Logger.LogInfo("Automatically requested level reward claim for level entry " + index + ".");
                    else
                        Logger.LogWarning("The game declined the automatic level reward claim for entry " + index + ".");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("Automatic level reward claim failed for entry " + index + ": " + ex.Message);
                }
                return;
            }
        }

        private void HandleRequest(BridgeRequest request, BridgeConnection connection)
        {
            if (request.method == "offline_summary.snapshot")
            {
                connection.Send(ProtocolJson.Response(request.id, true, _offlineProgress.GetSnapshotJson(), string.Empty), false);
                return;
            }
            if (request.method == "state.snapshot")
            {
                var snapshot = _state.CreateSnapshot();
                if (!snapshot.ready) connection.Send(ProtocolJson.Error(request.id, "not_ready", "The online market is not ready."), false);
                else connection.Send(ProtocolJson.Response(request.id, true, BridgeJson.SerializeSnapshot(snapshot), string.Empty), false);
                return;
            }
            if (request.method == "leaderboard.snapshot")
            {
                _leaderboards.Handle(request, connection);
                return;
            }
            if (request.method == "transactions.snapshot")
            {
                _transactionHistory.Handle(request, connection);
                return;
            }
            if (request.method == "state.subscribe")
            {
                connection.IsSubscribed = true;
                connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"subscribed\"}", string.Empty), false);
                var snapshot = _state.CreateSnapshot();
                if (snapshot.ready) connection.Send(ProtocolJson.Event("market.updated", BridgeJson.SerializeSnapshot(snapshot)), true);
                return;
            }
            if (request.method == "news.subscribe")
            {
                if (!_newsTicker.Available)
                {
                    connection.Send(ProtocolJson.Error(request.id, "news_unavailable",
                        "The game news ticker could not be hooked in this build."), false);
                    return;
                }
                connection.IsNewsSubscribed = true;
                connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"subscribed\"}", string.Empty), false);
                return;
            }
            if (request.method == "news.unsubscribe")
            {
                connection.IsNewsSubscribed = false;
                connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"unsubscribed\"}", string.Empty), false);
                return;
            }
            if (request.method == "auto_actions.subscribe_toasts")
            {
                if (!_autoActionToasts.Available)
                {
                    connection.Send(ProtocolJson.Error(request.id, "auto_action_toasts_unavailable",
                        "Auto-action completion toasts could not be hooked in this build."), false);
                    return;
                }
                connection.IsAutoActionToastSubscribed = true;
                connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"subscribed\"}", string.Empty), false);
                return;
            }
            if (request.method == "auto_actions.unsubscribe_toasts")
            {
                connection.IsAutoActionToastSubscribed = false;
                connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"unsubscribed\"}", string.Empty), false);
                return;
            }
            if (request.method == "market.human_activity")
            {
                connection.Send(_humanActivity.Handle(request), false);
                return;
            }
            if (request.method == "market.human_activity.set_focus")
            {
                if (!_humanActivityFocus.TrySetFocus(request.@params?.stockId ?? string.Empty,
                        out var focus, out var errorCode, out var errorMessage, out var retryAfterMs))
                {
                    connection.Send(ProtocolJson.Error(request.id, errorCode, errorMessage, retryAfterMs), false);
                    return;
                }
                connection.Send(ProtocolJson.Response(request.id, true,
                    BridgeJson.SerializeHumanActivityFocus(focus), string.Empty), false);
                return;
            }
            if (request.method == "market.human_activity.clear_focus")
            {
                if (!_humanActivityFocus.TryClearFocus(out var focus, out var errorCode, out var errorMessage, out var retryAfterMs))
                {
                    connection.Send(ProtocolJson.Error(request.id, errorCode, errorMessage, retryAfterMs), false);
                    return;
                }
                connection.Send(ProtocolJson.Response(request.id, true,
                    BridgeJson.SerializeHumanActivityFocus(focus), string.Empty), false);
                return;
            }
            if (request.method == "market.human_activity.subscribe")
            {
                SubscribeHumanActivity(request, connection);
                return;
            }
            if (request.method == "market.human_activity.unsubscribe")
            {
                UnsubscribeHumanActivity(request, connection);
                return;
            }
            if (request.method == "trade.submit")
            {
                connection.Send(_trades.Submit(request, connection), false);
                return;
            }
            if (request.method == "upgrades.snapshot" || request.method == "upgrades.purchase")
            {
                connection.Send(_upgrades.Handle(request.method, request), false);
                return;
            }
            if (request.method == "auto_actions.snapshot" || request.method == "auto_actions.add" ||
                request.method == "auto_actions.update" || request.method == "auto_actions.remove" ||
                request.method == "auto_actions.set_enabled" || request.method == "auto_actions.set_active")
            {
                connection.Send(_autoActions.Handle(request.method, request), false);
                return;
            }
            connection.Send(ProtocolJson.Error(request.id, "unknown_method", "Method is not supported."), false);
        }

        private void HandleRequestSafely(BridgeRequest request, BridgeConnection connection)
        {
            try
            {
                HandleRequest(request, connection);
            }
            catch (Exception ex)
            {
                Logger.LogError("Request '" + request.method + "' failed unexpectedly: " + ex);
                connection.Send(ProtocolJson.Error(request.id, "internal_error",
                    "The request failed because an unexpected game API error occurred."), false);
            }
        }

        private void SubscribeHumanActivity(BridgeRequest request, BridgeConnection connection)
        {
            var stockId = request.@params?.stockId ?? string.Empty;
            if (!_humanActivity.TryCreatePage(stockId, 0, 0L, out var page, out var errorCode, out var errorMessage))
            {
                connection.Send(ProtocolJson.Error(request.id, errorCode, errorMessage), false);
                return;
            }

            connection.SubscribeHumanActivity(stockId);
            var snapshot = BridgeJson.SerializeHumanActivityPage(page);
            if (!_lastHumanActivitySnapshots.ContainsKey(stockId))
                _lastHumanActivitySnapshots[stockId] = snapshot;
            connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"subscribed\"}", string.Empty), false);
            _server?.PublishHumanActivityTo(connection, snapshot);
        }

        private void UnsubscribeHumanActivity(BridgeRequest request, BridgeConnection connection)
        {
            var stockId = request.@params?.stockId ?? string.Empty;
            if (!RequestValidation.IsValidStockId(stockId))
            {
                connection.Send(ProtocolJson.Error(request.id, "invalid_stock", "stockId must be a non-empty stock identifier."), false);
                return;
            }

            connection.UnsubscribeHumanActivity(stockId);
            var server = _server;
            if (server == null || !server.HasHumanActivitySubscriber(stockId))
                _lastHumanActivitySnapshots.Remove(stockId);
            connection.Send(ProtocolJson.Response(request.id, true, "{\"status\":\"unsubscribed\"}", string.Empty), false);
        }

        private void OnDestroy()
        {
            OfflineProgressHarmonyPatch.CaptureService = null;
            OfflineProgressHarmonyPatch.AutoClose = false;
            TransactionHistoryHarmonyPatch.CaptureService = null;
            _transactionHistoryHarmony?.UnpatchSelf();
            _transactionHistoryHarmony = null;
            _harmony?.UnpatchSelf();
            _harmony = null;
            NewsTickerHarmonyPatch.CaptureService = null;
            _newsTickerHarmony?.UnpatchSelf();
            _newsTickerHarmony = null;
            _newsTicker.SetServer(null);
            AutoActionToastHarmonyPatch.CaptureService = null;
            _autoActionToastHarmony?.UnpatchSelf();
            _autoActionToastHarmony = null;
            _autoActionToasts.SetServer(null);
            if (!_humanActivityFocus.RestoreOnShutdown())
                Logger.LogWarning("Could not restore the game's activity focus while shutting down the plugin.");
            _server?.Dispose();
            _server = null;
        }

        private static GUIStyle? _humanActivityFocusOverlayStyle;

        private void OnGUI()
        {
            var stockId = _humanActivityFocus.ActiveStockId;
            if (string.IsNullOrEmpty(stockId)) return;

            var content = new GUIContent("Activity focus override: " + stockId);
            var style = _humanActivityFocusOverlayStyle;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false,
                };
                style.normal.textColor = Color.white;
                _humanActivityFocusOverlayStyle = style;
            }

            var size = style.CalcSize(content);
            var width = Mathf.Min(Mathf.Max(320f, size.x + 48f), Mathf.Max(64f, Screen.width - 32f));
            var height = Mathf.Max(64f, size.y + 28f);
            var rect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            var previousColor = GUI.color;
            GUI.color = new Color(0.035f, 0.045f, 0.065f, 1f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = previousColor;
            GUI.Label(rect, content, style);
        }

        private static string CreateToken()
        {
            var bytes = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
