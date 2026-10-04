using System.Collections.Generic;

namespace ScreenStocksBridge
{
    internal sealed class OfflineProgressCaptureService
    {
        private OfflineProgressSummaryDto? _latestSummary;

        internal void Capture(BigNumber total, BigNumber generators, BigNumber dividends, BigNumber autoActions,
            float secondsAway, float cappedEarningsSeconds, List<PositionChangeResult>? positionResults, bool showEarnings)
        {
            var summary = new OfflineProgressSummaryDto
            {
                total = StateService.FormatBigNumber(total),
                generators = StateService.FormatBigNumber(generators),
                dividends = StateService.FormatBigNumber(dividends),
                autoActions = StateService.FormatBigNumber(autoActions),
                secondsAway = secondsAway,
                cappedEarningsSeconds = cappedEarningsSeconds,
                showEarnings = showEarnings
            };

            if (positionResults != null)
            {
                foreach (var position in positionResults)
                {
                    if (position == null) continue;
                    summary.positionChanges.Add(new OfflinePositionChangeDto
                    {
                        stockId = position.StockId ?? string.Empty,
                        isLong = position.IsLong,
                        cashChange = StateService.FormatBigNumber(position.CashChange),
                        percentChange = position.PercentChange
                    });
                }
            }

            _latestSummary = summary;
        }

        internal string GetSnapshotJson()
        {
            return BridgeJson.SerializeOfflineProgressSnapshot(new OfflineProgressSnapshotDto
            {
                available = _latestSummary != null,
                summary = _latestSummary
            });
        }
    }
}
