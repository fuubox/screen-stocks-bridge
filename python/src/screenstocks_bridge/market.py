"""Typed views for synchronized clock evidence and canonical closed candles."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True)
class MarketClock:
    synchronized: bool
    server_time_ms: int | None
    market_tick: int
    tick_duration_ms: int
    market_epoch_ms: int
    dividend_interval_ms: int
    next_dividend_boundary_ms: int | None
    remaining_ms: int | None
    cycle_progress: float | None
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> MarketClock:
        return cls(bool(value["synchronized"]), value.get("serverTimeMs"), int(value["marketTick"]),
                   int(value["tickDurationMs"]), int(value["marketEpochMs"]), int(value["dividendIntervalMs"]),
                   value.get("nextDividendBoundaryMs"), value.get("remainingMs"), value.get("cycleProgress"), value)


@dataclass(frozen=True)
class MarketCandle:
    generation: str
    sequence: int
    stock_id: str
    bucket_start_ms: int
    revision: int
    open: float
    high: float
    low: float
    close: float
    quote_count: int
    first_source_time_ms: int
    last_source_time_ms: int
    first_tick: int
    last_tick: int
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> MarketCandle:
        return cls(str(value["generation"]), int(value["sequence"]), str(value["stockId"]),
                   int(value["bucketStartMs"]), int(value["revision"]), float(value["open"]),
                   float(value["high"]), float(value["low"]), float(value["close"]), int(value["quoteCount"]),
                   int(value["firstSourceTimeMs"]), int(value["lastSourceTimeMs"]),
                   int(value["firstTick"]), int(value["lastTick"]), value)
