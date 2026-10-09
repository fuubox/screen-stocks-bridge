# Changelog

Notable changes to Screen Stocks Bridge are recorded here. Versions follow
`MAJOR.MINOR.PATCH`; release tags use the matching `vMAJOR.MINOR.PATCH` format.

## [Unreleased]

## [0.12.0] - 2026-10-09

### Added

- IPO state snapshots and a guarded Python trigger that delegates to the game's
  own IPO method after its readiness, unlock, and eligibility checks.

### Verified

- Built against the installed demo and checked the required IPO method and data
  signatures against the current game assembly.
- IPO execution was not tested in-game because the demo save is still IPO-locked.

## [0.11.2] - 2026-10-09

### Fixed

- Refresh the game's claimed-level reward state after remote level data finishes
  loading, so the auto-actions unlock UI updates without requiring the player to
  close the level screen.
- Refresh inactive auto-action unlock UI components directly after that state
  sync, since their normal event subscription is disabled while their view is
  inactive.
- Update the pinned game compatibility fingerprint and check the level-refresh
  APIs used by the auto-actions unlock fix against the latest game patch.

## [0.11.0] - 2026-10-09

### Added

- Added current exact-string player net worth to the market snapshot and a
  throttled, cached API for the game's saved net-worth chart.

### Fixed

- Fixed the remote auto-action unlock check to honor a claimed unlock reward
  once the authoritative level catalog is available, even if the player-state
  entitlement flag is stale or false.

### Documentation

- Documented the net-worth history request behavior and remote auto-action
  unlock fallback.

## [0.10.0] - 2026-10-07

### Added

- Added page-level `fullScaleImpact` to human-activity queries and subscription
  events, with typed Python access through `HumanActivityPage.full_scale_impact`.
- Documented the source and processing of activity values, including bucket
  validation and alignment, derived totals, sample aggregation, and chart scaling.

### Verified

- Built the plugin and verified a live `$TECH` page returned
  `fullScaleImpact: 0.35`; the typed Python model returned the same value.
- Confirmed the latest eight `$TECH` samples in that live response had zero
  activity counts and impacts.

## [0.9.0] - 2026-10-06

### Added

- Added `effectiveMaxVolume` to stock snapshots, sourced from the game's own
  calculated cap. It includes purchased Stock Volume upgrades, level rewards,
  IPO/ascension bonuses, and authoritative remote market caps.
- Added exact-string access to the effective cap in the Python `Stock` model,
  alongside the distinct base `maxVolume` and current `availableShares` fields.
- Documented the meanings, progression behavior, precision, and Python access
  for all three volume fields.

### Verified

- Confirmed the installed game exposes the required accessor and the plugin
  builds against it.
- Confirmed a live bridge snapshot and typed Python model return the effective
  cap distinctly from base volume and available shares.
- The live player could not level up or IPO during verification, so a change in
  the calculated cap after those progression events was not directly tested.

## [0.8.0] - 2026-10-06

### Added

- Python subscription to newly initialized auto-action completion toasts via
  `subscribe_auto_action_toasts()` and `auto_action.toast` events.
- Toast text and structured action context: stock ID, action type, condition,
  and target price. The bridge forwards the game's localized TextMeshPro text
  without polling or making backend requests.
- End-user documentation with a complete listener example, event schema,
  callback-thread guidance, cleanup, and compatibility troubleshooting.

### Verified

- Captured a live toast and confirmed its event fields and raw rich-text text.
- Observed the displayed ticker `$BASE` differ from the structured stock ID
  `$PLAIN`; the guide explains this distinction and advises mapping through the
  live stock catalog rather than parsing localized text.
- Confirmed an authenticated `state.snapshot` succeeds when no subscription
  event is being sent and reports `ready: true`.

## [0.7.0] - 2026-10-05

### Added

- Python news ticker subscriptions with localized rendered headlines and
  structured market or scheduled-price announcement fields.
- Ticker capture reuses the game's in-memory rendering path without additional
  backend requests or polling for news.

### Verified

- Confirmed ticker event delivery against the running game.

## [0.6.0] - 2026-10-05

### Added

- Transaction history snapshots through the Python bridge, with both, manual,
  and auto_action filters and a configurable result limit up to 100.
- Cached transaction reads with stale-state reporting and minimum-enforced cache
  and backend-request intervals to prevent configuration from hammering the game.
- Larger bridge frames to carry complete transaction snapshots.

### Verified

- Live API read returned 100 transaction entries; a repeated query returned the
  cached snapshot, and the cross-filter request limit was reported.
- Built the plugin against the installed demo assemblies and confirmed the
  installed plugin/core DLL hashes.
- Live testing does not establish whether the first read reused a completed UI
  cache or joined a fresh in-flight game request.

## [0.5.0] - 2026-10-04

### Added

- Player and clan leaderboard reads through the game's in-process leaderboard
  client, with per-query caching and a global 30-second bridge request limit.
- Python `leaderboard()` convenience method and documentation for all six
  supported game modes, result fields, caching, and request behavior.

### Verified

- Confirmed live responses for all six modes: current, all-time, IPO, current
  top, clan net worth, and clan player share.
- Confirmed repeated queries use the cache and a different mode is rejected
  during the 30-second cooldown.
- Built against the installed demo, passed the game compatibility check, and
  passed the C# and Python client test suites.

## [0.4.0] - 2026-10-04

### Added

- Automatic welcome-back summary capture and optional screen dismissal, with
  session-memory access through `offline_summary()`. Auto-dismiss is enabled
  by default and can be disabled in BepInEx config.

### Verified

- Confirmed in the running game that the welcome-back popup auto-closed and the
  API returned its earnings breakdown, time away, and stock position changes.
- Built and installed the plugin against the demo's Unity and BepInEx
  assemblies; the installed DLL hash matched the build.

## [0.3.0] - 2026-10-04

### Added

- Per-stock aggregate human-activity subscriptions with initial and changed
  activity-page events, including support for multiple stocks per connection.
- Explicit single-stock human-activity focus override through Python, with an
  opaque centered in-game overlay, restoration behavior, and a five-second rate
  limit.

### Verified

- Confirmed live that manual graph selection cancels the override, rapid focus
  changes are rate-limited with a retry delay, and clearing restores the
  previous graph selection.
- Confirmed the centered overlay is clear and readable in the running game.
- Built and installed the plugin against the demo's Unity and BepInEx
  assemblies.

## [0.2.0] - 2026-10-03

### Added

- Dynamic upgrade catalog discovery, including game-provided descriptions,
  current and next values, next price, hidden state, and finite/unlimited/maxed
  status.
- Upgrade purchases delegated to the game's in-process purchase method, with a
  bounded quantity and explicit `submitted` status. The plugin does not connect
  directly to the backend; online requests are made by the game and remain
  server-authoritative.
- Python `upgrades()` and `purchase_upgrade()` convenience methods and guide
  hints describing the effects of the built-in upgrade IDs.

### Verified

- Built the plugin against the demo's Unity and BepInEx assemblies.
- Queried the live demo's upgrade catalog and submitted a one-level
  `DividendGain` purchase; a subsequent snapshot reported the increased level.

## [0.1.0] - 2026-10-03

Initial release for the Screen Stocks demo.

### Added

- BepInEx 5 plugin with an authenticated JSON API over loopback TCP and a Python
  3.10+ client with no third-party runtime dependencies.
- Live player and visible-market snapshots, including stock IDs, values,
  positions, market readiness, and manual-trade cooldown durations and status.
- Market change subscriptions and paged aggregate graph activity for visible
  stocks. Activity is aggregated; the API does not expose individual player
  trades or identities.
- Allowlisted buy, short, sell, cover, and close commands delegated to the
  game's in-process trade methods, with asynchronous completion events. Online
  server communication, when needed, is performed by the game.
- Dynamic auto-action slot discovery, action configuration and controls, and
  per-action cooldown reporting.
- Optional-by-setting direct claiming of available level rewards, enabled by
  default and independent of the Python bridge.
- Version-tagged GitHub release workflow producing an install-ready ZIP with a
  SHA-256 checksum, plus setup and API documentation.

### Verified

- Built the plugin in Release configuration against the demo's Unity and
  BepInEx assemblies.
- Connected the Python client to the running game and verified live snapshots,
  auto-action discovery, activity reads, and a small manual buy through the
  game's trade path.

Trade completion and all online decisions remain subject to the game's server
rules. Multiple trade submissions are not a guaranteed batch or queue.
