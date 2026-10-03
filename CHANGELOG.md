# Changelog

Notable changes to Screen Stocks Bridge are recorded here. Versions follow
`MAJOR.MINOR.PATCH`; release tags use the matching `vMAJOR.MINOR.PATCH` format.

## [0.2.0] - 2026-10-03

### Added

- Dynamic upgrade catalog discovery, including game-provided descriptions,
  current and next values, next price, hidden state, and finite/unlimited/maxed
  status.
- Upgrade purchases through the game's normal purchase method, with a bounded
  quantity and explicit `submitted` status for server-authoritative online
  purchases.
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
- Allowlisted buy, short, sell, cover, and close commands routed through the
  game's normal trade methods, with asynchronous completion events.
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
