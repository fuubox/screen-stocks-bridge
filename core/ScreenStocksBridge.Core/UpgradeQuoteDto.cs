namespace ScreenStocksBridge
{
    internal sealed class UpgradeQuoteDto
    {
        public string upgradeId = string.Empty;
        public string mode = string.Empty;
        public int quantity;
        public int levelBefore;
        public int levelAfter;
        public double currentValue;
        public double resultingValue;
        public string totalCost = "0";
        public string cashBefore = "0";
        public string remainingCash = "0";
        public string? budget;
        public bool affordable;
        public bool capReached;
        public bool truncated;
        public bool? targetReached;
        public int calculationLimit;
        public string stopReason = string.Empty;
        public string valueSource = string.Empty;
    }
}
