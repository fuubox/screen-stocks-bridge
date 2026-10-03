using System;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace ScreenStocksBridge
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "screenstocks.bridge";
        public const string PluginName = "Screen Stocks Python Bridge";
        public const string PluginVersion = "0.2.0";

        internal ConfigEntry<int> BridgePort { get; private set; } = null!;
        internal ConfigEntry<string> BridgeToken { get; private set; } = null!;
        internal ConfigEntry<bool> AutoClaimLevelRewards { get; private set; } = null!;
        private BridgeServer? _server;
        private readonly StateService _state = new StateService();
        private readonly HumanActivityService _humanActivity = new HumanActivityService();
        private readonly AutoActionsService _autoActions = new AutoActionsService();
        private readonly TradeService _trades = new TradeService();
        private readonly UpgradeService _upgrades = new UpgradeService();
        private float _nextSnapshotAt;
        private float _nextLevelClaimAt;
        private string _lastSnapshot = string.Empty;

        private void Awake()
        {
            BridgePort = Config.Bind("Bridge", "Port", 48721, "Loopback TCP port used by the Python bridge.");
            BridgeToken = Config.Bind("Bridge", "Token", string.Empty, "Secret required by local Python clients.");
            AutoClaimLevelRewards = Config.Bind("QualityOfLife", "AutoClaimLevelRewards", true,
                "Automatically claim available level rewards through the game's normal level-reward API.");

            if (string.IsNullOrWhiteSpace(BridgeToken.Value))
            {
                BridgeToken.Value = CreateToken();
                Config.Save();
                Logger.LogInfo("Generated Python bridge token: " + BridgeToken.Value);
            }

            Logger.LogInfo("Screen Stocks Python Bridge " + PluginVersion + " initialized.");

            if (BridgePort.Value < 1 || BridgePort.Value > 65535)
            {
                Logger.LogError("Bridge.Port must be between 1 and 65535; listener was not started.");
                return;
            }
            try
            {
                _server = new BridgeServer(BridgePort.Value, BridgeToken.Value);
                _server.Start();
                Logger.LogInfo("Loopback API listening on 127.0.0.1:" + BridgePort.Value + ".");
            }
            catch (Exception ex)
            {
                Logger.LogError("Could not start the loopback API: " + ex.Message);
                _server?.Dispose();
                _server = null;
            }
        }

        private void Update()
        {
            _server?.Drain(HandleRequest, 32);
            _trades.Update();
            TryAutoClaimLevelReward();
            if (_server == null || !_server.HasSubscribers || Time.unscaledTime < _nextSnapshotAt) return;
            _nextSnapshotAt = Time.unscaledTime + 0.25f;
            var snapshotDto = _state.CreateSnapshot();
            if (!snapshotDto.ready) return;
            var snapshot = BridgeJson.SerializeSnapshot(snapshotDto);
            if (string.Equals(snapshot, _lastSnapshot, StringComparison.Ordinal)) return;
            _lastSnapshot = snapshot;
            _server.Publish("market.updated", snapshot);
        }

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
            if (request.method == "state.snapshot")
            {
                var snapshot = _state.CreateSnapshot();
                if (!snapshot.ready) connection.Send(ProtocolJson.Error(request.id, "not_ready", "The online market is not ready."), false);
                else connection.Send(ProtocolJson.Response(request.id, true, BridgeJson.SerializeSnapshot(snapshot), string.Empty), false);
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
            if (request.method == "market.human_activity")
            {
                connection.Send(_humanActivity.Handle(request), false);
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

        private void OnDestroy()
        {
            _server?.Dispose();
            _server = null;
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
