using System;
using System.Collections.Generic;

namespace ScreenStocksBridge
{
    [Serializable]
    public sealed class StateSnapshotDto
    {
        public bool ready;
        public long serverTick;
        public string cash = "0";
        public int level;
        public List<StockDto> stocks = new List<StockDto>();
        public List<PositionDto> positions = new List<PositionDto>();
        public TradeCooldownsDto cooldowns = new TradeCooldownsDto();
        public AutoActionsSnapshotDto autoActions = new AutoActionsSnapshotDto();
    }

    [Serializable]
    public sealed class StockDto
    {
        public string stockId = string.Empty;
        public string name = string.Empty;
        public float price;
        public bool unlocked;
        public float basePrice;
        public float priceCap;
        public float dividendRate;
        public int maxVolume;
        public int availableShares;
    }

    [Serializable]
    public sealed class HumanActivityDto
    {
        public long sampleTick;
        public long up;
        public long down;
        public long total;
        public long net;
        public double upImpact;
        public double downImpact;
        public double totalImpact;
        public double netImpact;
    }

    [Serializable]
    public sealed class HumanActivityPageDto
    {
        public string stockId = string.Empty;
        public List<HumanActivityDto> samples = new List<HumanActivityDto>();
        public bool hasMore;
        public long nextBeforeTick;
    }

    [Serializable]
    public sealed class PositionDto
    {
        public string stockId = string.Empty;
        public string sharesOwned = "0";
        public string averageBuyPrice = "0";
        public string sharesShorted = "0";
        public string averageShortPrice = "0";
    }

    [Serializable]
    public sealed class TradeCooldownsDto
    {
        public long serverNowUnixSeconds;
        public bool serverClockSynchronized;
        public CooldownDto buy = new CooldownDto();
        public CooldownDto shortTrade = new CooldownDto();
    }

    [Serializable]
    public sealed class CooldownDto
    {
        public int durationSeconds;
        public long availableAtUnixSeconds;
        public long remainingSeconds;
        public bool active;
    }

    [Serializable]
    public sealed class AutoActionsSnapshotDto
    {
        public bool ready;
        public bool unlocked;
        public bool active;
        public int slotLimit;
        public int configuredCount;
        public bool canAddAction;
        public int cooldownDurationSeconds;
        public List<AutoActionDto> actions = new List<AutoActionDto>();
    }

    [Serializable]
    public sealed class AutoActionDto
    {
        public int slotIndex;
        public string stockId = string.Empty;
        public string actionType = string.Empty;
        public string condition = string.Empty;
        public float targetPrice;
        public int amountPercentage;
        public bool enabled;
        public long cooldownUntilUnixSeconds;
        public int cooldownDurationSeconds;
        public long cooldownRemainingSeconds;
        public bool onCooldown;
    }

    [Serializable]
    public sealed class UpgradesSnapshotDto
    {
        public bool ready;
        public List<UpgradeDto> upgrades = new List<UpgradeDto>();
    }

    [Serializable]
    public sealed class UpgradeDto
    {
        public string upgradeId = string.Empty;
        public string displayName = string.Empty;
        public string description = string.Empty;
        public bool hidden;
        public int currentLevel;
        public bool hasMaxLevel;
        public int? maxLevel;
        public int? remainingLevels;
        public bool maxed;
        public float currentValue;
        public float? nextValue;
        public string? nextPrice;
    }
}
