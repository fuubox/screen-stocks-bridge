using System;

namespace ScreenStocksBridge
{
    internal static class RequestValidation
    {
        internal static bool IsValidStockId(string? stockId) =>
            !string.IsNullOrWhiteSpace(stockId) && stockId.Length <= 128;

        internal static bool IsValidTradePercent(float percent) =>
            !float.IsNaN(percent) && !float.IsInfinity(percent) && percent > 0f && percent <= 100f;

        internal static bool IsValidUpgradeQuantity(int quantity) => quantity >= 1 && quantity <= 1000;
    }
}
