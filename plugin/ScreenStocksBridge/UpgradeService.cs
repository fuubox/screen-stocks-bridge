using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScreenStocksBridge
{
    internal sealed class UpgradeService
    {
        internal UpgradesSnapshotDto CreateSnapshot()
        {
            var result = new UpgradesSnapshotDto();
            var game = GameManager.I;
            var upgrades = game == null ? null : game.Upgrades;
            if (game == null || game.Data == null || upgrades == null || upgrades.stats == null) return result;

            result.ready = true;
            var seen = new HashSet<StatType>();
            foreach (var definition in upgrades.stats)
            {
                if (definition == null || !seen.Add(definition.stat)) continue;
                var level = Math.Max(0, game.GetUpgradeLevel(definition.stat));
                var maxed = game.IsUpgradeMaxed(definition.stat);
                var hasMaxLevel = !definition.infiniteLevels;
                var dto = new UpgradeDto
                {
                    upgradeId = definition.stat.ToString(),
                    displayName = definition.displayName ?? string.Empty,
                    description = definition.description ?? string.Empty,
                    hidden = definition.hide,
                    currentLevel = level,
                    hasMaxLevel = hasMaxLevel,
                    maxLevel = hasMaxLevel ? (int?)Math.Max(0, definition.maxLevel) : null,
                    remainingLevels = hasMaxLevel ? (int?)Math.Max(0, definition.maxLevel - level) : null,
                    maxed = maxed,
                    currentValue = game.GetUpgradeValue(definition.stat),
                    nextValue = maxed ? (float?)null : NextValue(game, upgrades, definition.stat, level),
                    nextPrice = maxed ? null : StateService.FormatBigNumber(game.GetUpgradePrice(definition.stat))
                };
                result.upgrades.Add(dto);
            }
            return result;
        }

        internal string Handle(string method, BridgeRequest request)
        {
            if (method == "upgrades.snapshot")
            {
                var snapshot = CreateSnapshot();
                if (!snapshot.ready) return ProtocolJson.Error(request.id, "not_ready", "The upgrade catalog is not initialized.");
                return ProtocolJson.Response(request.id, true, BridgeJson.SerializeUpgrades(snapshot), string.Empty);
            }

            var game = GameManager.I;
            if (game == null || game.Data == null || game.Upgrades == null || game.Upgrades.stats == null)
                return ProtocolJson.Error(request.id, "not_ready", "The upgrade catalog is not initialized.");

            var args = request.@params;
            var upgradeId = args?.upgradeId ?? string.Empty;
            if (string.IsNullOrWhiteSpace(upgradeId) || upgradeId.Length > 128 ||
                !Enum.TryParse(upgradeId, true, out StatType stat) || !Enum.IsDefined(typeof(StatType), stat) || stat == StatType.COUNT)
                return ProtocolJson.Error(request.id, "invalid_upgrade", "upgradeId must match a discovered upgradeId.");

            var definition = FindDefinition(game.Upgrades.stats, stat);
            if (definition == null) return ProtocolJson.Error(request.id, "upgrade_not_found", "No upgrade with that ID exists in the current catalog.");
            var quantity = args?.quantity ?? 1;
            if (!RequestValidation.IsValidUpgradeQuantity(quantity))
                return ProtocolJson.Error(request.id, "invalid_quantity", "quantity must be between 1 and 1000.");
            if (game.IsUpgradeMaxed(stat))
                return ProtocolJson.Error(request.id, "upgrade_maxed", "This upgrade is already maxed out.");

            var levelBefore = game.GetUpgradeLevel(stat);
            bool accepted;
            try { accepted = game.PurchaseUpgrades(stat, quantity); }
            catch (Exception ex) { return ProtocolJson.Error(request.id, "purchase_failed", ex.Message); }
            if (!accepted)
                return ProtocolJson.Error(request.id, "purchase_rejected", "The game declined the upgrade purchase request.");

            // Online purchases can be optimistic while the server decides the final amount.
            // Report submission and observed state; do not claim the requested quantity completed.
            var levelAfter = game.GetUpgradeLevel(stat);
            var response = "{\"status\":\"submitted\",\"upgradeId\":" + Quote(upgradeId) +
                ",\"requestedQuantity\":" + quantity.ToString(CultureInfo.InvariantCulture) +
                ",\"levelBefore\":" + levelBefore.ToString(CultureInfo.InvariantCulture) +
                ",\"levelAfter\":" + levelAfter.ToString(CultureInfo.InvariantCulture) + "}";
            return ProtocolJson.Response(request.id, true, response, string.Empty);
        }

        private static float NextValue(GameManager game, PlayerUpgradesSO upgrades, StatType stat, int level)
        {
            var currentBase = upgrades.GetValue(stat, level);
            var nextBase = upgrades.GetValue(stat, level + 1);
            return game.GetUpgradeValue(stat) + (nextBase - currentBase);
        }

        private static PlayerUpgradesSO.StatData? FindDefinition(List<PlayerUpgradesSO.StatData> definitions, StatType stat)
        {
            foreach (var definition in definitions)
                if (definition != null && definition.stat == stat) return definition;
            return null;
        }

        private static string Quote(string value)
        {
            // Reuse the bridge's JSON string escaping through the existing catalog serializer.
            return BridgeJson.Quote(value);
        }
    }
}
