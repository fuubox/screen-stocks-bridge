using System;
using UnityEngine;

namespace ScreenStocksBridge
{
    internal sealed class HumanActivityService
    {
        private const int DefaultPageSize = 32;
        private const int MaximumPageSize = 32;

        internal string Handle(BridgeRequest request)
        {
            var args = request.@params;
            var stockId = args?.stockId ?? string.Empty;
            var requestedLimit = args?.limit ?? 0;
            var beforeTick = args?.beforeTick ?? 0L;
            if (!TryCreatePage(stockId, requestedLimit, beforeTick, out var page, out var errorCode, out var errorMessage))
                return ProtocolJson.Error(request.id, errorCode, errorMessage);
            return ProtocolJson.Response(request.id, true, BridgeJson.SerializeHumanActivityPage(page), string.Empty);
        }

        internal bool TryCreatePage(string stockId, int requestedLimit, long beforeTick,
            out HumanActivityPageDto page, out string errorCode, out string errorMessage)
        {
            page = new HumanActivityPageDto { stockId = stockId };
            errorCode = string.Empty;
            errorMessage = string.Empty;

            if (!RequestValidation.IsValidStockId(stockId))
            {
                errorCode = "invalid_stock";
                errorMessage = "stockId must be a non-empty stock identifier.";
                return false;
            }
            if (requestedLimit < 0 || requestedLimit > MaximumPageSize)
            {
                errorCode = "invalid_limit";
                errorMessage = "limit must be between 1 and 32 when specified.";
                return false;
            }

            var game = GameManager.I;
            var manager = StockManager.I;
            if (game == null || game.Data == null || manager == null || manager.Source == null || !manager.Source.IsReady)
            {
                errorCode = "not_ready";
                errorMessage = "The online market is not ready.";
                return false;
            }
            if (!game.IsStockVisibleToPlayer(stockId))
            {
                errorCode = "stock_unavailable";
                errorMessage = "Stock is not visible in this edition.";
                return false;
            }

            var remoteSource = manager.Source as RemoteMarketDataSource;
            if (remoteSource == null)
            {
                errorCode = "activity_unavailable";
                errorMessage = "Human activity history is not available from the current market source.";
                return false;
            }

            try
            {
                var activity = manager.GetHumanActivity(stockId);
                var ticks = remoteSource.GetHistorySampleTicks(stockId);
                if (activity == null || ticks == null)
                    return true;

                var alignedCount = Math.Min(activity.Count, ticks.Count);
                var end = alignedCount;
                if (beforeTick > 0)
                {
                    while (end > 0 && ticks[end - 1] >= beforeTick) end--;
                }

                var limit = requestedLimit == 0 ? DefaultPageSize : requestedLimit;
                var start = Math.Max(0, end - limit);
                for (var i = start; i < end; i++)
                {
                    var sample = activity[i];
                    page.samples.Add(new HumanActivityDto
                    {
                        sampleTick = ticks[i],
                        up = sample.Up,
                        down = sample.Down,
                        total = sample.Total,
                        net = sample.Net,
                        upImpact = sample.UpImpact,
                        downImpact = sample.DownImpact,
                        totalImpact = sample.TotalImpact,
                        netImpact = sample.NetImpact
                    });
                }
                page.hasMore = start > 0;
                page.nextBeforeTick = page.hasMore ? ticks[start] : 0L;
                return true;
            }
            catch (Exception ex)
            {
                errorCode = "activity_failed";
                errorMessage = "Could not read human activity: " + ex.Message;
                return false;
            }
        }
    }
}
