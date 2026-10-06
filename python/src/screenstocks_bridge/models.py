"""Optional typed views over bridge dictionaries.

Each model retains its source dictionary as ``raw`` so callers can access fields
added by later protocol versions without waiting for a client release.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True)
class HumanActivity:
    sample_tick: int
    up: int
    down: int
    total: int
    net: int
    up_impact: float
    down_impact: float
    total_impact: float
    net_impact: float
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> HumanActivity:
        return cls(int(value.get("sampleTick", 0)), int(value.get("up", 0)),
                   int(value.get("down", 0)), int(value.get("total", 0)),
                   int(value.get("net", 0)), float(value.get("upImpact", 0)),
                   float(value.get("downImpact", 0)), float(value.get("totalImpact", 0)),
                   float(value.get("netImpact", 0)), value)


@dataclass(frozen=True)
class Stock:
    stock_id: str
    name: str
    price: float
    unlocked: bool
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> Stock:
        return cls(str(value.get("stockId", "")), str(value.get("name", "")),
                   float(value.get("price", 0)), bool(value.get("unlocked", False)),
                   value)

    @property
    def max_volume(self) -> int:
        """Base stock volume cap, before player progression bonuses."""
        return int(self.raw.get("maxVolume", 0))

    @property
    def effective_max_volume(self) -> str:
        """Game-calculated cap including purchased upgrades, levels, and IPOs."""
        return str(self.raw.get("effectiveMaxVolume", "0"))

    @property
    def available_shares(self) -> int:
        """Shares currently available from market supply; not the player's cap."""
        return int(self.raw.get("availableShares", 0))


@dataclass(frozen=True)
class HumanActivityPage:
    stock_id: str
    samples: list[HumanActivity]
    has_more: bool
    next_before_tick: int
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> HumanActivityPage:
        return cls(str(value.get("stockId", "")),
                   [HumanActivity.from_dict(item) for item in value.get("samples", [])],
                   bool(value.get("hasMore", False)), int(value.get("nextBeforeTick", 0)), value)


@dataclass(frozen=True)
class Position:
    stock_id: str
    shares_owned: str
    average_buy_price: str
    shares_shorted: str
    average_short_price: str
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> Position:
        return cls(str(value.get("stockId", "")), str(value.get("sharesOwned", "0")),
                   str(value.get("averageBuyPrice", "0")), str(value.get("sharesShorted", "0")),
                   str(value.get("averageShortPrice", "0")), value)


@dataclass(frozen=True)
class Cooldown:
    duration_seconds: int
    available_at_unix_seconds: int
    remaining_seconds: int
    active: bool
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> Cooldown:
        return cls(int(value.get("durationSeconds", 0)),
                   int(value.get("availableAtUnixSeconds", 0)),
                   int(value.get("remainingSeconds", 0)), bool(value.get("active", False)), value)


@dataclass(frozen=True)
class TradeCooldowns:
    server_now_unix_seconds: int
    server_clock_synchronized: bool
    buy: Cooldown
    short_trade: Cooldown
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> TradeCooldowns:
        return cls(int(value.get("serverNowUnixSeconds", 0)),
                   bool(value.get("serverClockSynchronized", False)),
                   Cooldown.from_dict(value.get("buy", {})),
                   Cooldown.from_dict(value.get("shortTrade", {})), value)


@dataclass(frozen=True)
class AutoAction:
    slot_index: int
    stock_id: str
    action_type: str
    condition: str
    target_price: float
    amount_percentage: int
    enabled: bool
    cooldown_until_unix_seconds: int
    cooldown_duration_seconds: int
    cooldown_remaining_seconds: int
    on_cooldown: bool
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> AutoAction:
        return cls(int(value.get("slotIndex", -1)), str(value.get("stockId", "")),
                   str(value.get("actionType", "")), str(value.get("condition", "")),
                   float(value.get("targetPrice", 0)), int(value.get("amountPercentage", 0)),
                   bool(value.get("enabled", False)), int(value.get("cooldownUntilUnixSeconds", 0)),
                   int(value.get("cooldownDurationSeconds", 0)),
                   int(value.get("cooldownRemainingSeconds", 0)),
                   bool(value.get("onCooldown", False)), value)


@dataclass(frozen=True)
class AutoActions:
    ready: bool
    unlocked: bool
    active: bool
    slot_limit: int
    configured_count: int
    can_add_action: bool
    cooldown_duration_seconds: int
    actions: list[AutoAction]
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> AutoActions:
        return cls(bool(value.get("ready", False)), bool(value.get("unlocked", False)),
                   bool(value.get("active", False)), int(value.get("slotLimit", 0)),
                   int(value.get("configuredCount", 0)), bool(value.get("canAddAction", False)),
                   int(value.get("cooldownDurationSeconds", 0)),
                   [AutoAction.from_dict(item) for item in value.get("actions", [])], value)


@dataclass(frozen=True)
class Snapshot:
    ready: bool
    server_tick: int
    cash: str
    level: int
    stocks: list[Stock]
    positions: list[Position]
    cooldowns: TradeCooldowns
    auto_actions: AutoActions
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> Snapshot:
        return cls(bool(value.get("ready", False)), int(value.get("serverTick", 0)),
                   str(value.get("cash", "0")), int(value.get("level", 0)),
                   [Stock.from_dict(item) for item in value.get("stocks", [])],
                   [Position.from_dict(item) for item in value.get("positions", [])],
                   TradeCooldowns.from_dict(value.get("cooldowns", {})),
                   AutoActions.from_dict(value.get("autoActions", {})), value)


@dataclass(frozen=True)
class TradeReceipt:
    status: str
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> TradeReceipt:
        return cls(str(value.get("status", "")), value)
