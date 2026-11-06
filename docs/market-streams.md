# Market clock, raw prices, and one-second candles

The bridge exposes the game's clock, sampled player/market snapshots, an optional
raw websocket price tunnel, and a separate canonical candle stream. It uses the
game's existing connection. Reads and subscriptions do not cause extra game-server
requests or submit trades.

## Controls and events

Canonical candle consumers require bridge/SDK 0.16.0 or newer. The
`market.stream_status` response advertises `bridgeVersion` and
`candleSchemaVersion: 1`; check these alongside `hooksAvailable` before ingesting.
The release number alone does not establish that native hooks are operational.

| Operation | Parameters | Meaning |
| --- | --- | --- |
| `market.stream_status` | None | Global cadence, instant mode, hook availability, source and generation status |
| `market.set_rate` | `rateHz` | Set a finite positive global snapshot cadence; default 4 Hz |
| `market.set_instant` | `enabled` | Toggle global raw price forwarding independently of snapshot cadence |
| `state.subscribe` | None | Initial full snapshot and clock/status; then changed snapshots, clock, payout and optional raw price events |
| `market.candles.subscribe` | Optional `generation`, `afterSequence`, `candleMode` | Replay retained closed-candle updates, then subscribe to new updates; mode defaults to `observed` |
| `market.candles.unsubscribe` | None | Stop this connection's candle events |

Settings apply to current and future subscribers and reset to defaults when the
plugin reloads. There is no additional positive-rate cap: actual snapshot checks
cannot exceed Unity's update loop. High requested rates can increase CPU work.
The rate controls sampling, not a guaranteed event rate. Full `market.updated`
events are sent only for material changes. Prices, money, positions, configuration,
deadlines, clock synchronization and feed transitions count; market tick alone,
clock progression, manual/auto cooldown countdowns, and dividend progress do not.
Direct snapshot queries still return the current countdown values.

Python helpers are `market_stream_status()`, `set_market_rate(rate_hz)`,
`set_instant_mode(enabled)`, `subscribe_market(callback)`,
`subscribe_candles(callback, generation="", after_sequence=0)`, and
`unsubscribe_candles()`. Callbacks run on the event worker. Inspect event names;
a registered callback can receive several subscribed streams.

## Clock and dividend schedule

Ready state snapshots include `marketClock`. `market.clock` carries the same
compact clock record approximately once per second while state subscribers are
active (subject to Unity frame cadence):

- `synchronized`, `serverTimeMs`, `marketTick`, and `tickDurationMs`.
- `marketEpochMs`, `dividendIntervalMs`, `nextDividendBoundaryMs`, `remainingMs`,
  and `cycleProgress`.
- `clockProvenance: "game_server_clock"` and
  `scheduleProvenance: "local_game_settings"`.

The server time is the game's estimate. Epoch, interval and tick duration come
from local settings in the reviewed game build; they are not new remote settings
queries. Zero configuration values mean settings are unavailable. When the game
clock is unsynchronized, `serverTimeMs`, the next boundary, remaining time and
progress are null; local wall time is never labeled as synchronized server time.
An invalid interval also makes the timer unavailable. At an exact boundary, the
next scheduled boundary is the following cycle. A timer reaching zero is not a
payout confirmation. Python exposes an optional `MarketClock` typed view.

`market.dividend_paid` is emitted only from an accepted authoritative realtime
`passivePayout` confirmation for the current player data epoch, deduplicated by
epoch and boundary. General dividend/prediction callbacks cannot produce it.
Its fields are `dataEpoch`, `boundaryIndex`, `scheduledBoundaryMs`,
`dividendAmount` (an exact decimal string), and `observedServerTimeMs` (null when
unsynchronized). The native boundary is a cycle index, so its timestamp is epoch
plus index times interval. A confirmation requiring a state refresh still records
the confirmed payout; it does not assert that the UI balance has already updated.
HTTP state refreshes do not fabricate individual payout events from balance deltas.

## Raw price tunnel

Instant mode sends `market.price` to state subscribers in addition to ordinary
snapshots. Each event's data contains:

- `sourceGeneration`: changes when the source/session resets.
- `replayedBaseline`: true only for a cached baseline sent to bootstrap a client.
- `sourceJson`: the exact original JSON text passed to the game's message parser,
  preserving whitespace, source sequence, timestamp, tick, layout and all original
  source fields. Decode it separately if you want the original message object.

Only `priceVectorBaseline`, `priceVector`, and legacy `prices` messages that reach
the native accepted price path can be forwarded. Raw player state, authentication,
payouts and other control messages are excluded. The bridge captures the text
before decoding and observes accepted prices afterward; it does not rebuild raw
messages from sampled state or from the decoded callback.

The exact accepted vector baseline is cached even with instant mode off. Enabling
instant mode sends existing subscribers a fresh full snapshot followed by that
baseline before forwarding subsequent vectors. A subscriber joining while enabled
gets the same bootstrap ordering (clock/status may appear between snapshot and
baseline). A normal snapshot cannot substitute for the baseline's layout mapping.
If the baseline is unknown, `market.feed_status.waitingForBaseline` is true and
vector forwarding waits for a real matching baseline. It does not request or invent
one. Legacy `prices` messages carry stock IDs and do not need a vector baseline.

Replayed baselines establish layout; their old prices must not overwrite newer
quotes or be counted as new observations. Baseline replay never creates candle
observations in the bridge. A baseline newly received from
the server is a source observation; each valid price entry counts once. Layouts
are cleared on source reset or websocket disconnect. Source sequences can have
gaps due to non-price messages and are distinct from bridge delivery sequences.

## Canonical closed candles

`market.candle` is a separate subscription, independent of instant mode and the
full snapshot cadence. Each candle is one stock's observations in the UTC interval
`[bucketStartMs, bucketEndMs)`, where the start is the source timestamp floored to
one second. Prices are the game's decoded float values. Source timestamps and
ticks are retained; local receipt timestamps and HTTP samples are not used.

Fields include `generation`, `sequence`, `stockId`, `bucketStartMs`, `bucketEndMs`,
`revision`, `open`, `high`, `low`, `close`, `quoteCount`,
`quoteCountKind: "source_observations"`, `firstSourceTimeMs`, `lastSourceTimeMs`,
`firstTick`, and `lastTick`. Python exposes `MarketCandle.from_dict(data)`.

Open and close use source time, then source tick, then accepted arrival order
to break ties. High/low cover all valid observations. Repeated unchanged prices
count as observations; this is not trade volume. Invalid/nonpositive prices or
invalid source timestamps/ticks do not enter candles. Seconds without observations
are omitted, rather than filled with synthetic flat bars.

### Connected candles

Bridge and SDK 0.17.0 introduce connected mode.
Choose `candleMode: "connected"` on the subscription, or Python
`subscribe_candles(callback, candle_mode="connected")`, to use the same stock's
previous observed candle's close as the next open, including across empty seconds
when the bucket starts are at most **15 seconds apart**. High/low include that
carried price and all current observations; close remains the last current
observation. Longer gaps and generation resets break the connection. The
first observed second after either starts at its own first observation. No bars
are invented, and the carried open does not add to `quoteCount` or change source
timestamp/tick evidence. A late predecessor insertion or close correction revises
the next observed closed candle within that limit, recomputing its bounds from
current observations. `openSourceBucketStartMs` identifies the candle supplying
the open (the current bucket for `source_observation`, the predecessor for
`previous_close`). A carried open remains fixed when its source expires from
retention; retained predecessors can still revise it. No predecessor objects are
retained through reference chains.

Subscriptions are independent: selecting connected candles does not change
another client's observed candles, raw prices, or snapshot cadence. Each mode has
its own generation, sequence, and replay log. Start with a fresh cursor when
switching; a cursor from the other mode reports a gap. `market.stream_status`
advertises `candleModes: ["observed", "connected"]`; check this capability before
requesting connected mode from an older bridge. Unsupported mode names return
`invalid_params`.

Candle events and subscription replies identify `candleMode`; events also carry
`openProvenance` (`previous_close` or `source_observation`). Connection feed-status
events identify the selected mode and its generation. Python's typed
`MarketCandle` exposes `candle_mode` and `open_provenance`. The default `observed`
mode retains its existing source-only OHLC semantics.

Only closed candles are emitted. While synchronized, server time closes intervals;
otherwise a later accepted source timestamp proves that an earlier second ended.
The watermark never moves backward. HTTP fallback pauses raw output and candle
publication; ordinary state snapshots continue. On websocket recovery, any still
retained source bars can close without inventing observations during the gap.

The window is 15 seconds behind the watermark, retaining whole candle intervals
that intersect it. Accepted late quotes inside that time window revise and re-emit
their closed bar with a higher revision. Quotes older than the watermark minus
15 seconds are excluded and produce `market.candle_late` with source timestamp,
tick and cumulative late-observation count. The watermark advances using source
timestamps and, when synchronized, server time, including during fallback.

## Replay and delivery loss

Candle cursors combine candle generation (`market.feed_status.candleGeneration`)
and candle update sequence. Subscribe
with the last cursor to replay updates after it. A fresh subscription (empty
generation and sequence zero) replays the available window. The reply contains
`generation`, `latestSequence`, `gap`, and `replayCount`. Changed/expired/future
cursors and holes left by expired corrections set `gap: true` and replay the
remaining available window. Replay includes emitted revisions, not just the latest
version of each bar. Replace bars by stock/second and increasing revision; reset
that mirror when generation changes. Game-source/player-session resets clear the
buffer. Websocket transport disconnects change the raw source generation while
preserving the time-bounded candle buffer for recovery.

Replay is paced in small batches before later live candle updates on that
connection and pauses during HTTP fallback. If a pending replay expires before
delivery, `market.candle_gap` reports `reason: "replay_expired"` and
`resubscribeRequired: true`; request a fresh replay window. Retention is bounded in memory: the replay log and each pending replay
have a 65,536-update limit as well as the time window. Log eviction is detectable
as a cursor gap; an overflowing pending replay closes its connection. There is no
disk storage or historical game-server backfill.

Market events have top-level `delivery` metadata outside `data` and `sourceJson`:
`generation` (connection session), `stream`, `sequence`, and cumulative
`droppedEvents`. Source sequence, candle sequence and delivery sequence have
different purposes. Track all three only where relevant to your consumer.

For connections without activity v2 subscriptions, event delivery is best effort.
When the queue evicts an event, its original stream gets the drop count. A priority
`stream.loss` notification with `origin: "bridge"` bypasses that bounded queue,
so a quiet stream does not hide loss indefinitely. If activity v2 is subscribed
on the connection, its stronger disconnect-on-overflow rule takes precedence.
Oversized frames also close the connection instead of silently truncating data.

The Python client reports its own queue loss with `stream.loss`,
`origin: "python_client"`, and cumulative per-stream counts. Handle loss even if
no later event arrives. Recover candles by cursor, query current snapshots, and
rebootstrap raw layouts as appropriate. These are observations of loss, not a
lossless-delivery guarantee. Reconnect with a fresh client instance and explicitly
resubscribe; connection-owned activity pins must be reclaimed separately.

`market.feed_status` reports websocket, HTTP fallback or unavailable source, source
and candle generations, current settings, baseline-waiting state, and the last
capture error type if one occurred. Native capture faults invalidate continuity
and close affected connections without throwing bridge errors into the game.
