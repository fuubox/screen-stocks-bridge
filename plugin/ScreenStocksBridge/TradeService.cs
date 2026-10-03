using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class TradeService
    {
        private readonly Dictionary<string, PendingTrade> _pending = new Dictionary<string, PendingTrade>();
        private static readonly HashSet<string> Actions = new HashSet<string>(StringComparer.Ordinal)
        {
            "buy_max", "buy_percent", "short_max", "short_percent", "close_max", "close_percent",
            "sell_max", "sell_percent", "cover_max", "cover_percent"
        };

        internal string Submit(BridgeRequest request, BridgeConnection connection)
        {
            var args = request.@params;
            var action = args?.action ?? string.Empty;
            var stockId = args?.stockId ?? string.Empty;
            var game = GameManager.I;
            var manager = StockManager.I;
            if (!Actions.Contains(action)) return ProtocolJson.Error(request.id, "unknown_action", "Action is not in the trade allowlist.");
            if (string.IsNullOrWhiteSpace(stockId) || stockId.Length > 128) return ProtocolJson.Error(request.id, "invalid_stock", "stockId must be a non-empty stock identifier.");
            if (game == null || manager == null || game.Data == null || manager.Source == null || !manager.Source.IsReady)
                return ProtocolJson.Error(request.id, "not_ready", "The online market is not ready.");
            if (!game.IsStockVisibleToPlayer(stockId) || !game.IsStockUnlocked(stockId))
                return ProtocolJson.Error(request.id, "stock_unavailable", "Stock is not visible and unlocked in this edition.");
            if (!manager.CanStartManualTrade || manager.IsManualTradeHeld(stockId))
                return ProtocolJson.Error(request.id, "trade_busy", "A manual trade is held or the trade queue is full.");

            var isPercent = action.EndsWith("_percent", StringComparison.Ordinal);
            var isMax = action.EndsWith("_max", StringComparison.Ordinal);
            var percent = args?.percent ?? 0f;
            if (isPercent && (float.IsNaN(percent) || float.IsInfinity(percent) || percent <= 0f || percent > 100f))
                return ProtocolJson.Error(request.id, "invalid_percent", "percent must be finite and in the range (0, 100].");
            if (!isPercent && !isMax) return ProtocolJson.Error(request.id, "invalid_action", "Trade action is malformed.");

            var position = game.Data.GetPosition(stockId);
            if (action.StartsWith("sell_", StringComparison.Ordinal) &&
                (position == null || position.sharesOwned.CompareTo(BigNumber.Zero) <= 0))
                return ProtocolJson.Error(request.id, "no_long_position", "No long shares are available to sell or close.");
            if (action.StartsWith("cover_", StringComparison.Ordinal) &&
                (position == null || position.sharesShorted.CompareTo(BigNumber.Zero) <= 0))
                return ProtocolJson.Error(request.id, "no_short_position", "No short shares are available to cover.");
            if (action.StartsWith("close_", StringComparison.Ordinal) && (position == null ||
                (position.sharesOwned.CompareTo(BigNumber.Zero) <= 0 && position.sharesShorted.CompareTo(BigNumber.Zero) <= 0)))
                return ProtocolJson.Error(request.id, "no_position", "No long or short shares are available to close.");
            if (action.StartsWith("short_", StringComparison.Ordinal) && manager.GetAvailableShares(stockId).CompareTo(BigNumber.Zero) <= 0)
                return ProtocolJson.Error(request.id, "no_short_volume", "No shares are currently available to short.");

            Task<bool> task;
            try { task = Dispatch(action, stockId, percent, position); }
            catch (Exception ex) { return ProtocolJson.Error(request.id, "dispatch_failed", ex.Message); }
            _pending[request.id] = new PendingTrade(request.id, action, stockId, task, connection);
            return ProtocolJson.Response(request.id, true, "{\"status\":\"submitted\"}", string.Empty);
        }

        private static Task<bool> Dispatch(string action, string stockId, float percent, StockPosition? position)
        {
            var manager = StockManager.I;
            if (action == "buy_max") return manager.BuyMaxAsync(stockId);
            if (action == "short_max") return manager.ShortMaxAsync(stockId);
            if (action == "buy_percent") return manager.BuyPercentAsync(stockId, PercentInt(percent));
            if (action == "short_percent") return manager.ShortPercentAsync(stockId, PercentInt(percent));

            var fraction = action.EndsWith("_percent", StringComparison.Ordinal) ? Math.Max(0f, Math.Min(1f, percent / 100f)) : (float?)null;
            if (action.StartsWith("close_", StringComparison.Ordinal))
            {
                var longs = position?.sharesOwned ?? BigNumber.Zero;
                var shorts = position?.sharesShorted ?? BigNumber.Zero;
                if (fraction.HasValue)
                {
                    var ratio = (long)Math.Round(fraction.Value * 10000f, MidpointRounding.AwayFromZero);
                    longs = BigNumber.MulDiv(longs, ratio, 10000, true);
                    shorts = BigNumber.MulDiv(shorts, ratio, 10000, true);
                }
                return manager.CloseSharesAsync(stockId, longs, shorts, fraction);
            }

            if (action.StartsWith("sell_", StringComparison.Ordinal))
            {
                var amount = position!.sharesOwned;
                if (fraction.HasValue) amount = BigNumber.MulDiv(amount, (long)Math.Round(fraction.Value * 10000f, MidpointRounding.AwayFromZero), 10000, true);
                return manager.SellAsync(stockId, amount, fraction);
            }

            var shortAmount = position!.sharesShorted;
            if (fraction.HasValue) shortAmount = BigNumber.MulDiv(shortAmount, (long)Math.Round(fraction.Value * 10000f, MidpointRounding.AwayFromZero), 10000, true);
            return manager.CoverAsync(stockId, shortAmount, fraction);
        }

        private static int PercentInt(float percent) => Math.Max(1, Math.Min(100, (int)Math.Round(percent, MidpointRounding.AwayFromZero)));

        internal void Update()
        {
            if (_pending.Count == 0) return;
            var completed = new List<string>();
            foreach (var pair in _pending)
            {
                var pending = pair.Value;
                if (!pending.Task.IsCompleted) continue;
                var success = false;
                var reason = string.Empty;
                try { success = pending.Task.Status == TaskStatus.RanToCompletion && pending.Task.Result; }
                catch (Exception ex) { reason = ex.GetBaseException().Message; }
                if (!success && string.IsNullOrEmpty(reason)) reason = "Game trade command was rejected.";
                var payload = BridgeJson.SerializeTradeCompleted(pending.RequestId, pending.Action, pending.StockId,
                    success ? "completed" : "rejected", reason);
                pending.Connection.Send(ProtocolJson.Event("trade.completed", payload), false);
                completed.Add(pair.Key);
            }
            foreach (var id in completed) _pending.Remove(id);
        }

        private sealed class PendingTrade
        {
            internal readonly string RequestId;
            internal readonly string Action;
            internal readonly string StockId;
            internal readonly Task<bool> Task;
            internal readonly BridgeConnection Connection;
            internal PendingTrade(string requestId, string action, string stockId, Task<bool> task, BridgeConnection connection)
            { RequestId = requestId; Action = action; StockId = stockId; Task = task; Connection = connection; }
        }
    }
}
