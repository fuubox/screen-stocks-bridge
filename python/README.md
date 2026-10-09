# Screen Stocks Python Bridge

The `screenstocks-bridge` package is a Python client for the local API exposed
by the Screen Stocks Bridge BepInEx plugin. It requires the plugin to be
installed in the game and the game to be running. The package itself does not
install or contact the game backend; it connects to the plugin on loopback.

## Install

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
    print(bridge.upgrades())
    recent = bridge.transaction_history(filter="both", limit=100)
    print(recent["entries"])
    chart = bridge.net_worth_history("last_7_days")
    print(chart["intervalMinutes"], chart["samples"])
```

The client supports market-state reads, market, ticker-news, and auto-action toast
subscriptions, leaderboard requests, transaction-history and net-worth chart reads,
activity reads and subscriptions, explicit single-stock activity-focus controls,
trades, upgrade purchases, auto-action controls, and IPO state and trigger requests.
Use `ipo_snapshot()` to check readiness and the dynamic requirement, and
`trigger_ipo()` to ask the game to perform an eligible IPO. Request a game-supported
leaderboard with `leaderboard("current")`,
`leaderboard("all_time")`, `leaderboard("ipo")`, `leaderboard("current_top")`,
`leaderboard("clan_net_worth")`, or `leaderboard("clan_player_share")`. Leaderboard
requests use the game's in-process client, with one live request every 30 seconds;
identical requests are served from a 30-second cache. Activity focus can be set with `set_human_activity_focus(stock_id)`
and cleared with `clear_human_activity_focus()`; the in-game overlay shows the
active stock, and bridge-driven focus changes have a five-second minimum. See the
[bridge usage guide](https://github.com/fuubox/screen-stocks-bridge/blob/main/docs/bridge-usage.md)
for the full API and its online behavior. Activity history reads page through
only the samples the game currently retains in memory; they do not fetch older
history from the game's server, and the bridge makes no guarantee about the
retained history's total depth or time span.

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
responses use the game's own client, are cached in memory for at least 60
seconds, and have a hard 30-second minimum between requests across ranges.
Those settings can be raised under `[NetWorthHistory]`; successful Net Worth
screen responses also seed the cache. See the usage guide for fields, examples,
and details about native game UI requests.

Use `offline_summary()` to read the latest welcome-back summary captured during
the current game session. The plugin captures it in memory before automatically
closing the screen; set `AutoCloseOfflineSummary = false` in the BepInEx config
to leave the screen open while retaining API access.
