"""Activity v2 typed state and an atomic, gap-detecting local replica.

Snapshots and changes describe the game's retained collection. They are not new
trade flow. Histogram grouping/rendering and durable capture belong to callers.
"""
from __future__ import annotations

from copy import deepcopy
from dataclasses import dataclass, field
from typing import Any


class ActivityResyncRequired(RuntimeError):
    """Discard partial changes and obtain a fresh activity snapshot."""


@dataclass(frozen=True)
class ActivityBucket:
    bucket_id: int
    up: int
    down: int
    up_impact: float
    down_impact: float
    placement_sample_tick: int | None
    raw: dict[str, Any] = field(repr=False)

    @classmethod
    def from_dict(cls, row: dict[str, Any]) -> ActivityBucket:
        tick = row.get("placementSampleTick")
        return cls(int(row["bucketId"]), int(row["up"]), int(row["down"]),
                   float(row["upImpact"]), float(row["downImpact"]),
                   None if tick is None else int(tick), deepcopy(row))


@dataclass(frozen=True)
class ActivitySnapshot:
    generation: str
    revision: int
    state: dict[str, Any]
    rows: dict[str, dict[str, Any]]
    observed_at_utc: str | None = None
    response_age_seconds: float | None = None
    value_change_age_seconds: float | None = None

    @property
    def buckets(self) -> list[ActivityBucket]:
        return sorted((ActivityBucket.from_dict(row) for row in self.rows.values() if row["kind"] == "bucket"),
                      key=lambda bucket: bucket.bucket_id)

    @property
    def samples(self):
        from .models import HumanActivity
        return sorted((HumanActivity.from_dict(row) for row in self.rows.values() if row["kind"] == "sample"),
                      key=lambda sample: sample.sample_tick)

    @property
    def stock_id(self) -> str:
        return str(self.state.get("stockId", ""))

    @property
    def owner_session_id(self) -> str | None:
        return self.state.get("ownerSessionId")

    @property
    def focus_mode(self) -> str:
        return str(self.state["focusMode"])

    def is_observing(self, stock_id: str) -> bool:
        return self.stock_id == stock_id and self.state.get("hasAcceptedData") is True

    @classmethod
    def from_pages(cls, pages: list[dict[str, Any]]) -> ActivitySnapshot:
        if not pages:
            raise ActivityResyncRequired("Snapshot has no pages.")
        first = pages[0]
        rows = {}
        offset = 0
        for index, page in enumerate(pages):
            if (page.get("schemaVersion") != 2 or page.get("snapshotId") != first.get("snapshotId")
                    or page.get("generation") != first.get("generation")
                    or page.get("revision") != first.get("revision")
                    or page.get("state") != first.get("state") or page.get("offset") != offset):
                raise ActivityResyncRequired("Snapshot pages do not share a consistent revision/cursor.")
            for row in page["rows"]:
                if row["key"] in rows:
                    raise ActivityResyncRequired("Duplicate snapshot row.")
                rows[row["key"]] = deepcopy(row["value"])
            expected = offset + len(page["rows"])
            next_offset = page.get("nextOffset")
            if next_offset is None:
                if index != len(pages) - 1:
                    raise ActivityResyncRequired("Unexpected page after snapshot end.")
            elif next_offset != expected or next_offset <= offset:
                raise ActivityResyncRequired("Snapshot cursor did not advance correctly.")
            offset = expected
        if pages[-1].get("nextOffset") is not None:
            raise ActivityResyncRequired("Snapshot is incomplete.")
        return cls(str(first["generation"]), int(first["revision"]), deepcopy(first["state"]), rows,
                   first.get("observedAtUtc"), first.get("responseAgeSeconds"), first.get("valueChangeAgeSeconds"))


class ActivityReplica:
    """Apply each revision atomically after its final ordered chunk.

    The caller serializes access. Queue events arriving while fetching a snapshot,
    then replay them: revisions already included in the snapshot are ignored.
    On transport loss, discard this replica and explicitly resubscribe/reclaim.
    """

    def __init__(self, snapshot: ActivitySnapshot):
        self.snapshot = deepcopy(snapshot)
        self._pending: list[dict[str, Any]] = []
        self._invalid = False

    def apply(self, change: dict[str, Any]) -> bool:
        if self._invalid:
            raise ActivityResyncRequired("Replica was invalidated; replace it from a snapshot.")
        try:
            return self._apply(change)
        except (KeyError, TypeError, ValueError, ActivityResyncRequired) as exc:
            self._invalid = True
            self._pending.clear()
            if isinstance(exc, ActivityResyncRequired):
                raise
            raise ActivityResyncRequired("Invalid activity change.") from exc

    def _apply(self, change: dict[str, Any]) -> bool:
        if change.get("schemaVersion") != 2 or change["generation"] != self.snapshot.generation:
            raise ActivityResyncRequired("Activity generation/version changed.")
        if change["revision"] <= self.snapshot.revision:
            return False
        if (change["previousRevision"] != self.snapshot.revision
                or change["revision"] != self.snapshot.revision + 1
                or change["chunkIndex"] != len(self._pending)
                or not 1 <= change["chunkCount"] <= 65536):
            raise ActivityResyncRequired("Activity continuity was lost.")
        if self._pending and any(change[key] != self._pending[0][key]
                                 for key in ("generation", "revision", "previousRevision", "chunkCount", "state")):
            raise ActivityResyncRequired("Activity chunks are inconsistent.")
        self._pending.append(deepcopy(change))
        if len(self._pending) < change["chunkCount"]:
            return False
        if len(self._pending) != change["chunkCount"]:
            raise ActivityResyncRequired("Invalid activity chunk count.")
        rows = deepcopy(self.snapshot.rows)
        for chunk in self._pending:
            for operation in chunk["changes"]:
                if operation["op"] == "upsert":
                    rows[operation["key"]] = deepcopy(operation["value"])
                elif operation["op"] == "remove":
                    rows.pop(operation["key"], None)
                else:
                    raise ActivityResyncRequired("Unknown activity operation.")
        self.snapshot = ActivitySnapshot(change["generation"], change["revision"], deepcopy(change["state"]), rows)
        self._pending.clear()
        return True
