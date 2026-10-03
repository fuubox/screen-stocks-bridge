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
            if (string.IsNullOrWhiteSpace(stockId) || stockId.Length > 128)
                return ProtocolJson.Error(request.id, "invalid_stock", "stockId must be a non-empty stock identifier.");
            if (requestedLimit < 0 || requestedLimit > MaximumPageSize)
                return ProtocolJson.Error(request.id, "invalid_limit", "limit must be between 1 and 32 when specified.");

            var game = GameManager.I;
            var manager = StockManager.I;
            if (game == null || game.Data == null || manager == null || manager.Source == null || !manager.Source.IsReady)
                return ProtocolJson.Error(request.id, "not_ready", "The online market is not ready.");
            if (!game.IsStockVisibleToPlayer(stockId))
                return ProtocolJson.Error(request.id, "stock_unavailable", "Stock is not visible in this edition.");

            var remoteSource = manager.Source as RemoteMarketDataSource;
            if (remoteSource == null)
                return ProtocolJson.Error(request.id, "activity_unavailable", "Human activity history is not available from the current market source.");

            try
            {
                var activity = manager.GetHumanActivity(stockId);
                var ticks = remoteSource.GetHistorySampleTicks(stockId);
                var page = new HumanActivityPageDto { stockId = stockId };
                if (activity == null || ticks == null)
                    return ProtocolJson.Response(request.id, true, BridgeJson.SerializeHumanActivityPage(page), string.Empty);

                var alignedCount = Math.Min(activity.Count, ticks.Count);
                var end = alignedCount;
                var beforeTick = args?.beforeTick ?? 0L;
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
                return ProtocolJson.Response(request.id, true, BridgeJson.SerializeHumanActivityPage(page), string.Empty);
            }
            catch (Exception ex)
            {
                return ProtocolJson.Error(request.id, "activity_failed", "Could not read human activity: " + ex.Message);
            }
        }
    }
}
