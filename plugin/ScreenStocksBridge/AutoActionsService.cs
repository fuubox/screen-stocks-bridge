using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class AutoActionsService
    {
        internal AutoActionsSnapshotDto CreateSnapshot()
        {
            var result = new AutoActionsSnapshotDto();
            var game = GameManager.I;
            var manager = AutoActionManager.I;
            if (game == null || game.Data == null || manager == null) return result;

            result.ready = true;
            result.unlocked = game.IsAutoActionsUnlocked();
            result.active = game.Data.autoActionsActive;
            result.slotLimit = Math.Max(0, manager.GetMaxActions());
            result.cooldownDurationSeconds = AutoCooldownDuration(game);
            var actions = manager.GetAllActions();
            result.configuredCount = actions == null ? 0 : actions.Count;
            result.canAddAction = result.unlocked && manager.CanAddAction();
            var now = ScreenStocks.Net.ServerClock.UnixSeconds;
            if (actions != null)
            {
                foreach (var action in actions)
                {
                    if (action == null) continue;
                    var remaining = Math.Max(0L, action.cooldownUntilTimestamp - now);
                    result.actions.Add(new AutoActionDto
                    {
                        slotIndex = action.slotIndex,
                        stockId = action.stockId ?? string.Empty,
                        actionType = action.actionType.ToString(),
                        condition = action.condition.ToString(),
                        targetPrice = action.targetPrice,
                        amountPercentage = action.NormalizedAmountPercentage,
                        enabled = action.enabled,
                        cooldownUntilUnixSeconds = action.cooldownUntilTimestamp,
                        cooldownDurationSeconds = result.cooldownDurationSeconds,
                        cooldownRemainingSeconds = remaining,
                        onCooldown = remaining > 0
                    });
                }
            }
            return result;
        }

        internal string Handle(string method, BridgeRequest request)
        {
            if (method == "auto_actions.snapshot")
            {
                var snapshot = CreateSnapshot();
                if (!snapshot.ready) return ProtocolJson.Error(request.id, "not_ready", "Auto actions are not initialized.");
                return ProtocolJson.Response(request.id, true, BridgeJson.SerializeAutoActions(snapshot), string.Empty);
            }

            var game = GameManager.I;
            var manager = AutoActionManager.I;
            if (game == null || game.Data == null || manager == null)
                return ProtocolJson.Error(request.id, "not_ready", "Auto actions are not initialized.");
            if (!game.IsAutoActionsUnlocked())
                return ProtocolJson.Error(request.id, "auto_actions_locked", "Auto actions are not unlocked for this player.");

            var args = request.@params;
            if (method == "auto_actions.add") return Add(request.id, args, game, manager);
            if (method == "auto_actions.update") return Update(request.id, args, game, manager);
            if (method == "auto_actions.remove") return Remove(request.id, args, manager);
            if (method == "auto_actions.set_enabled") return SetEnabled(request.id, args, manager);
            if (method == "auto_actions.set_active")
            {
                var active = args?.active ?? false;
                manager.SetActionsActive(active);
                return ProtocolJson.Response(request.id, true, "{\"status\":\"updated\",\"active\":" + (active ? "true" : "false") + "}", string.Empty);
            }
            return ProtocolJson.Error(request.id, "unknown_method", "Auto-action method is not supported.");
        }

        private static string Add(string id, RequestParams? args, GameManager game, AutoActionManager manager)
        {
            if (!TryValidateConfiguration(args, game, out var actionType, out var condition, out var message))
                return ProtocolJson.Error(id, "invalid_action", message);
            if (!manager.CanAddAction())
                return ProtocolJson.Error(id, "no_free_slot", "No auto-action slot is currently available.");

            var listIndex = manager.AddAction();
            if (listIndex < 0) return ProtocolJson.Error(id, "no_free_slot", "The game could not allocate an auto-action slot.");
            var action = manager.GetAction(listIndex);
            if (action == null) return ProtocolJson.Error(id, "add_failed", "The game added no readable auto action.");
            ApplyConfiguration(action, args!, actionType, condition);
            manager.NotifyActionEdited();
            return ProtocolJson.Response(id, true, "{\"status\":\"added\",\"slotIndex\":" + action.slotIndex.ToString(CultureInfo.InvariantCulture) + "}", string.Empty);
        }

        private static string Update(string id, RequestParams? args, GameManager game, AutoActionManager manager)
        {
            if (args == null || args.slotIndex < 0) return ProtocolJson.Error(id, "invalid_slot", "slotIndex must identify an existing auto-action slot.");
            if (!TryValidateConfiguration(args, game, out var actionType, out var condition, out var message))
                return ProtocolJson.Error(id, "invalid_action", message);
            var listIndex = FindListIndex(manager, args.slotIndex);
            if (listIndex < 0) return ProtocolJson.Error(id, "slot_not_found", "No auto action occupies that slot.");
            var action = manager.GetAction(listIndex);
            if (action == null) return ProtocolJson.Error(id, "slot_not_found", "No auto action occupies that slot.");
            manager.NotifyActionEditing();
            ApplyConfiguration(action, args, actionType, condition);
            manager.NotifyActionEdited();
            return ProtocolJson.Response(id, true, "{\"status\":\"updated\",\"slotIndex\":" + action.slotIndex.ToString(CultureInfo.InvariantCulture) + "}", string.Empty);
        }

        private static string Remove(string id, RequestParams? args, AutoActionManager manager)
        {
            if (args == null || args.slotIndex < 0) return ProtocolJson.Error(id, "invalid_slot", "slotIndex must identify an existing auto-action slot.");
            var listIndex = FindListIndex(manager, args.slotIndex);
            if (listIndex < 0) return ProtocolJson.Error(id, "slot_not_found", "No auto action occupies that slot.");
            manager.RemoveAction(listIndex);
            return Success(id, "removed", args.slotIndex);
        }

        private static string SetEnabled(string id, RequestParams? args, AutoActionManager manager)
        {
            if (args == null || args.slotIndex < 0) return ProtocolJson.Error(id, "invalid_slot", "slotIndex must identify an existing auto-action slot.");
            var listIndex = FindListIndex(manager, args.slotIndex);
            if (listIndex < 0) return ProtocolJson.Error(id, "slot_not_found", "No auto action occupies that slot.");
            manager.SetActionEnabled(listIndex, args.enabled);
            return ProtocolJson.Response(id, true, "{\"status\":\"updated\",\"slotIndex\":" + args.slotIndex.ToString(CultureInfo.InvariantCulture) + ",\"enabled\":" + (args.enabled ? "true" : "false") + "}", string.Empty);
        }

        private static bool TryValidateConfiguration(RequestParams? args, GameManager game, out AutoActionType actionType, out ActionCondition condition, out string error)
        {
            actionType = AutoActionType.Buy;
            condition = ActionCondition.Above;
            error = string.Empty;
            if (args == null) { error = "Auto-action parameters are required."; return false; }
            if (!Enum.TryParse(args.actionType, true, out actionType) || !Enum.IsDefined(typeof(AutoActionType), actionType))
            { error = "actionType must be Buy, Short, CloseBuy, or CloseShort."; return false; }
            if (!Enum.TryParse(args.condition, true, out condition) || !Enum.IsDefined(typeof(ActionCondition), condition))
            { error = "condition must be Above or Below."; return false; }
            if (!RequestValidation.IsValidStockId(args.stockId) || !game.IsStockVisibleToPlayer(args.stockId) || !game.IsStockUnlocked(args.stockId))
            { error = "stockId must be visible and unlocked in this edition."; return false; }
            if (float.IsNaN(args.targetPrice) || float.IsInfinity(args.targetPrice) || args.targetPrice <= 0f)
            { error = "targetPrice must be finite and greater than zero."; return false; }
            if (args.amountPercentage < 1 || args.amountPercentage > 100)
            { error = "amountPercentage must be between 1 and 100."; return false; }
            return true;
        }

        private static void ApplyConfiguration(AutoAction action, RequestParams args, AutoActionType actionType, ActionCondition condition)
        {
            action.stockId = args.stockId;
            action.actionType = actionType;
            action.condition = condition;
            action.targetPrice = args.targetPrice;
            action.amountPercentage = args.amountPercentage;
        }

        private static int FindListIndex(AutoActionManager manager, int slotIndex)
        {
            var actions = manager.GetAllActions();
            for (var i = 0; actions != null && i < actions.Count; i++)
                if (actions[i] != null && actions[i].slotIndex == slotIndex) return i;
            return -1;
        }

        private static int AutoCooldownDuration(GameManager game) => Mathf.CeilToInt(Mathf.Max(0f, game.GetUpgradeValue(StatType.AutoActionCooldown)));

        private static string Success(string id, string status, int value) =>
            ProtocolJson.Response(id, true, "{\"status\":\"" + status + "\",\"slotIndex\":" + value.ToString(CultureInfo.InvariantCulture) + "}", string.Empty);
    }
}
