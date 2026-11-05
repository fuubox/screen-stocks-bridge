# Activity v2

Activity v2 replaces `market.human_activity*`. Those legacy operations now return
`unsupported_activity_version`. Other bridge operations are unchanged. Every
`activity.*` request must include `schemaVersion: 2` in its parameters.

This is a breaking change. Complete the linked trading-desk migration before
installing the replacement bridge in a working setup. Building/testing does not
install the plugin or restart the game or desk.

Tracking: [bridge redesign #2](https://github.com/fuubox/screen-stocks-bridge/issues/2)
and required [desk migration #57](https://github.com/fuubox/screen-stocks-trading-desk/issues/57).

## Reading and observing

The game observes one activity stock globally. By default it follows graph
selection. Reads and subscriptions never select a stock. State always identifies
the effective `stockId`; if it differs from the stock you want, that stock is
**not currently observed**. A ready connection alone does not establish accepted
activity data; inspect `hasAcceptedData` as well.

| Method | Parameters beyond schemaVersion | Result |
| --- | --- | --- |
| `activity.snapshot` | Optional `snapshotId`, `offset` | One immutable snapshot page |
| `activity.subscribe` | None | First snapshot page; starts passive `activity.changed` events |
| `activity.unsubscribe` | None | Stops this connection's activity events; does not unpin |
| `activity.pin` | `stockId` | Claims or changes this connection's pin; returns first snapshot page |
| `activity.takeover` | `stockId` | Explicitly replaces an existing owner; returns first snapshot page |
| `activity.unpin` | None | Owner releases immediately; returns first snapshot page |

`activity.pin` returns `ownership_conflict` if another connection owns the pin.
An explicit takeover is subject to the same five-second limit as a deliberate
pin change. Rejected requests preserve ownership and focus. Repeating the owner's
same-stock pin is idempotent. Unpin and detected owner disconnect bypass the
cooldown and return to the graph currently selected. A former owner's disconnect
cannot release a replacement owner's pin. Reconnection creates a new ownership
session: reclaiming a saved preference requires an explicit request and may fail
with an ownership conflict. Authentication tokens are never owner identifiers.

## Snapshot contract

A page contains `schemaVersion`, `generation`, `revision`, `state`, `snapshotId`,
`offset`, `nextOffset`, and `rows`. Pages contain at most 32 rows. Each row has a
stable `key` and a `value`. `nextOffset: null` means the snapshot is complete.
Continue with the same snapshot ID and returned offset. One snapshot per
connection is retained for 30 seconds; starting another replaces it. Expired,
reset, or superseded cursors return `snapshot_expired`; restart at offset zero.
Invalid offsets return `invalid_cursor`. Pages never silently switch revisions.

State includes:

- `stockId`, `focusMode` (`follow_graph` or `pinned`), and `ownerSessionId` (null
  when unowned).
- `ready`, `hasAcceptedData`, and `connectionState` (`ready`, `not_ready`, or
  `unknown`). Connection state describes the game's existing realtime client;
  fallback HTTP responses can still arrive when realtime is not ready.
- `ticksPerBucket` and `fullScaleImpact` (null until accepted data is available).
- `lastResponseAtUtc`, `lastValueChangeAtUtc`, and `lastServerTick`. Receipt time
  is a bridge observation of an accepted response/delta, not a server event time.
- `oldestRetainedBucketId`, when an accepted delta supplies it, and
  `retentionBoundsKnown`. A full response does not supply this lower bound.
  `coverageComplete` remains null: knowing retention bounds does not prove
  complete observations or confirmed inactivity throughout the interval.

The page also includes `observedAtUtc`, `responseAgeSeconds`, and
`valueChangeAgeSeconds`. Ages use a monotonic clock and refer to the state
captured in that snapshot, not newer activity received between continuation
pages. Unknown ages/timestamps remain null. Consumers choose warning thresholds;
the bridge does not equate source silence with inactivity or a failed feed.

Rows have two representations:

- Bucket rows: `kind: "bucket"`, `bucketId`, `up`, `down`, `upImpact`,
  `downImpact`, and `placementSampleTick`. The interval is
  `[bucketId * ticksPerBucket, (bucketId + 1) * ticksPerBucket - 1]` on the
  game-tick axis. Keys include an occurrence suffix to preserve duplicate IDs
  from an accepted full response. Native duplicate suppression and placement
  determine which occurrence contributes; noncontributing/unplaced occurrences
  have null placement.
- Sample rows: `kind: "sample"`, `sampleTick`, `up`, `down`, `total`, `net`,
  `upImpact`, `downImpact`, `totalImpact`, and `netImpact`. Values come from the
  game's aligner. Keys identify the sample tick.

Use bucket placement to distinguish an explicit zero-valued bucket from a
zero-filled graph position with no placed bucket. Neither alone establishes
server-wide inactivity. Original bucket values are preserved, while native
validation, duplicate suppression, saturating arithmetic, and placement rules
govern aligned samples. Initial capture after plugin load can expose existing
accepted data with unknown receipt time until another response arrives.

## Ordered changes and recovery

`activity.changed` data contains `schemaVersion`, `generation`, `revision`,
`previousRevision`, `state`, `chunkIndex`, `chunkCount`, and `changes`. A change
is either `{"op":"upsert","key":...,"value":...}` or
`{"op":"remove","key":...}`. There are at most 32 operations per chunk.
Upserts replace rows, rather than adding repeated values. Changes cover the full
game-retained collection, including corrections outside the latest 32 samples.
Source metadata changes can produce a revision with no row changes.

Buffer all chunks and commit a revision only after receiving every ordered chunk
with matching metadata. The predecessor must match your current revision. On a
generation change, missing/reordered chunk, or invalid predecessor, discard
partial changes and obtain a fresh snapshot. Focus/client resets invalidate old
generations. Retired clients cannot update the current feed.

Subscribe first, buffer events while completing its returned snapshot, then
replay buffered changes. Ignore revisions already represented by the snapshot.
Pagination describes bounded game-retained state, not a durable history store.

The server closes an activity-subscribed connection rather than silently dropping
an outbound event at queue overflow. The Python client similarly closes on its
event queue overflow or transport loss and emits `activity.resync_required`.
This notification does not depend on another event arriving. Discard the replica,
reconnect, and explicitly resubscribe; any pin must also be claimed anew. The
client does not automatically reclaim authority or retry a takeover.
Create a fresh `BridgeClient` instance when reconnecting so event workers and
queued observations from the previous transport session cannot be reused.

The Python package exports `ActivitySnapshot`, `ActivityBucket`,
`ActivityReplica`, and `ActivityResyncRequired`. `activity_snapshot()` returns a
complete typed snapshot; `subscribe_activity(callback)` returns its first page.
Pass that page to `activity_snapshot(first_page)` to complete the initial read.
`ActivityReplica.apply(event["data"])` commits complete revisions or raises
`ActivityResyncRequired`. Serialize access to a replica. Typed samples use the
existing `HumanActivity` model; the former paged activity model is legacy.
Catch `ActivityResyncRequired` inside your callback and schedule recovery there;
the event worker isolates callback exceptions. Typed snapshot observation ages
describe the captured snapshot; after applying changes, use the updated source
timestamps in state or query a new snapshot for new monotonic-age evidence.

## Reproducing a histogram

Consumers choose their own viewport and grouping. To follow the native math:

1. Select the visible aligned samples and group neighboring positions into bars.
   The game's grouping depends on available width and minimum bar spacing.
2. Sum total and net impacts within each group.
3. When any visible group has positive impact, divide each grouped total impact
   by the largest visible grouped total impact.
4. If no visible impact is positive but counts are positive, use grouped total
   and net counts instead and normalize by the largest grouped total count.
5. Derive direction/intensity from grouped net divided by grouped total, clamped
   to `[-1, 1]`. Native rendering blends neutral toward the up/down color.

The game clamps height fractions and applies minimum visible pixel height. Those
presentation choices belong to the consumer. The server's `fullScaleImpact` is
a reference with the native `0.2` fallback for invalid/nonpositive values; it is
not the ordinary positive-impact bar-height denominator. For grouped impacts
`0.01` and `0.02` with reference `0.2`, native height fractions before pixel
adjustments are `0.5` and `1.0`, whereas server-reference percentages are `5%`
and `10%`. Peak/latest summaries are separate descriptive measures.

No bridge-generated bars, viewport geometry contract, extra server requests,
individual trade records, or all-time history are provided.
