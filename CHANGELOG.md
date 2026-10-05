# Changelog

Notable changes to Screen Stocks Bridge are recorded here. Versions follow
`MAJOR.MINOR.PATCH`; release tags use the matching `vMAJOR.MINOR.PATCH` format.

## [Unreleased]

### Added

- Python news ticker subscriptions that deliver the game's localized rendered
  headlines and structured market or scheduled-price announcement fields.
- Ticker capture reuses the game's in-memory rendering path without making
  additional backend requests or polling for news.

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
