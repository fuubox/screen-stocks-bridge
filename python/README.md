# Screen Stocks Python Bridge

The `screenstocks-bridge` package is a Python client for the local API exposed
by the Screen Stocks Bridge BepInEx plugin. It requires the plugin to be
installed in the game and the game to be running. The package itself does not
contact the game's online server; it connects to the plugin in the local game
process over loopback.

## Install

Before installing, follow the [Python package installation security note](https://github.com/fuubox/screen-stocks-bridge/blob/main/docs/python-install-security.md)
for the October 2026 certificate incident. Use `pip` 26.2.2 or later; upgrade
an older pip with the hash-pinned bootstrap instructions before installing
packages.

```powershell
python -m pip install screenstocks-bridge
```

Install the matching BepInEx plugin from the
[GitHub releases](https://github.com/fuubox/screen-stocks-bridge/releases),
then launch Screen Stocks. The default bridge address is `127.0.0.1:48721`. The plugin port can be changed under `[Bridge]` as `Port` in `BepInEx/config/screenstocks.bridge.cfg`; close and relaunch the game after editing it. Pass the same value to `BridgeClient(port=...)` when it differs from the default. The example scripts also read `SCREENSTOCKS_PORT`.
Read the token from `BepInEx/config/screenstocks.bridge.cfg` or the BepInEx
log.

## Connect

```python
from screenstocks_bridge import BridgeClient

with BridgeClient(token="YOUR_TOKEN") as bridge:
    state = bridge.snapshot()
    print(state["ready"], state["cash"], state["netWorth"])
    stats = bridge.stats_snapshot()
    print(stats["lifetimeBuyingProfit"], stats["lifetimeShortingProfit"])
    print(bridge.upgrades())
    recent = bridge.transaction_history(filter="both", limit=100)
    print(recent["entries"])
    chart = bridge.net_worth_history("last_7_days")
    print(chart["intervalMinutes"], chart["samples"])
```

The client supports market-state and stats reads, market, ticker-news, and auto-action toast
subscriptions, leaderboard requests, transaction-history and net-worth chart reads,
activity reads and subscriptions, explicit single-stock activity-focus controls,
trades, upgrade quotes and purchases, auto-action controls, and IPO state and trigger requests.
Use `quote_upgrade("DividendGain", cash_fraction=0.5)` to preview how many levels
fit half your cash, their total cost, and the resulting effective value without
buying anything. Other selectors are `quantity`, `budget` (a decimal string),
`target_level`, and `target_value`. Inspect `truncated` and `stopReason` when the
quote reaches its calculation bound (up to 5,000 levels).
For `quantity=5` (+5 levels), response fields, raw RPC examples, and error handling,
see the [upgrade quoter guide](https://github.com/fuubox/screen-stocks-bridge/blob/main/docs/upgrade-quoter.md).
Use `ipo_snapshot()` to check readiness and the dynamic requirement, and
`trigger_ipo()` to ask the game to perform an eligible IPO. Request a game-supported
leaderboard with `leaderboard("current")`,
`leaderboard("all_time")`, `leaderboard("ipo")`, `leaderboard("current_top")`,
`leaderboard("clan_net_worth")`, or `leaderboard("clan_player_share")`. Leaderboard
requests use the game's in-process client, with one live request every 30 seconds;
identical requests are served from a 30-second cache. Activity v2 uses
`pin_activity(stock_id)`, `takeover_activity(stock_id)`, and `unpin_activity()`
for connection-owned focus; subscriptions are passive. Read complete typed state
with `activity_snapshot()`, and use `subscribe_activity(callback)` with
`ActivityReplica` for ordered changes. The previous human-activity operations
are unsupported. See the [activity v2 migration guide](../docs/activity-v2.md)
for source freshness, immutable pagination, histogram rules, and recovery.

For a complete, runnable auto-action toast listener—including the event schema,
callback threading, cleanup, and error handling—see [Subscribe to auto-action
completion toasts](../docs/bridge-usage.md#subscribe-to-auto-action-completion-toasts).

Use `transaction_history(filter="both", limit=100)` to read recent transactions.
Filters are `manual`, `both`, and `auto_action`. The game screen can seed the
in-memory cache; successful screen requests also start the bridge cooldown. The
BepInEx cache lifetime and request interval can be increased from their 60-second
and 30-second minimums, as described in the usage guide.

`snapshot()` includes `netWorth` as an exact decimal string from the game's
current in-memory calculation. `net_worth_history("last_24_hours")` can query
the saved chart for `last_24_hours`, `last_7_days`, or `last_14_days`. Chart
responses use the game's own client, are cached in memory for at least 300
seconds, and have a hard 300-second minimum between requests across ranges,
matching the game's stated five-minute graph refresh cadence.
Those settings can be raised under `[NetWorthHistory]`; successful Net Worth
screen responses also seed the cache. See the usage guide for fields, examples,
and details about native game UI requests.

The snapshot also includes the game's calculated `dividendIncomePerMinute` and
`dividendPayoutPerCycle` as exact decimal strings. Per-stock `dividendRate`
includes the current game dividend payout multipliers, while
`baseDividendRate` preserves the unmodified stock-definition rate. These rates
do not include price, owned shares, or payout timing; use the aggregate fields
for the actual income totals. In Python, access them as `stock.dividend_rate`
and `stock.base_dividend_rate`.

Use `offline_summary()` to read the latest welcome-back summary captured during
the current game session. The plugin captures it in memory before automatically
closing the screen; set `AutoCloseOfflineSummary = false` in the BepInEx config
to leave the screen open while retaining API access.
