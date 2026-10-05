# Screen Stocks Bridge Guide

This guide answers the current questions about discovering game state and using the Python bridge for trades and auto actions.

## Unity, BepInEx, and the game's file-based mod API

The Screen Stocks demo is a Unity Mono game. The bridge runs as a BepInEx 5 plugin inside the game and exposes a separate local API to Python.

The official support mechanism discussed earlier is tied to the game's `modSupport` setting and the `thesmallshort_online_settings.sav` file-based interface. The bridge does not enable that setting, access that file, or redirect that filesystem API. It does not write API snapshots, trade requests/results, market exports, or command files. Instead, it reads live game objects in memory and sends JSON over an authenticated TCP connection bound to `127.0.0.1`. BepInEx still writes its normal config and logs, and the game can persist normal game settings. In particular, changing auto-action configuration uses the game's own manager APIs and may be saved by the game.

### Network boundary

The bridge itself does not connect to the game's backend or implement its network protocol. Its socket listener binds only to `127.0.0.1` for the local Python client. For commands, the plugin calls in-process game methods such as `StockManager` trade methods, `GameManager.PurchaseUpgrades`, or the level-claim method. In online mode those game methods may send requests to the game's server themselves; that network traffic belongs to the game, not to a backend client implemented by this plugin.

## Connect from Python

Install the published Python client with Python 3.10 or later:

```powershell
python -m pip install screenstocks-bridge
```

This installs only the Python client. Install the BepInEx plugin separately and launch the game before connecting. For development from a repository checkout, use `python -m pip install -e .\python` instead.

Copy the token from `BepInEx\config\screenstocks.bridge.cfg` or the BepInEx log, then connect:

```python
from screenstocks_bridge import BridgeClient

with BridgeClient(host="127.0.0.1", port=48721, token="YOUR_TOKEN") as bridge:
    state = bridge.snapshot()
```

The default host and port are `127.0.0.1:48721`. Calls require the game and plugin to be running and the online market to be ready.

The plugin's listener port is configurable in `BepInEx\config\screenstocks.bridge.cfg`, under `[Bridge]`:

```ini
[Bridge]
Port = 48721
```

Edit `Port` while the game is closed, then restart the game for the setting to take effect. The plugin accepts ports from 1 through 65535 and binds only to `127.0.0.1`. If the selected port is unavailable, the listener fails to start and reports the error in the BepInEx log. When using a non-default port, pass the same value to `BridgeClient`, for example `BridgeClient(host="127.0.0.1", port=49321, token="YOUR_TOKEN")`, or set `SCREENSTOCKS_PORT` when running the example scripts.

Under `[QualityOfLife]`, `AutoCloseOfflineSummary = true` (the default) captures and then closes the game's welcome-back screen. Set it to `false` to leave that screen open; the plugin still captures its data for the API.

## Read the welcome-back summary

When the game displays its welcome-back screen, the plugin captures the structured values supplied to that screen before closing it. The most recent capture stays in plugin memory for the current game session. It is not written to disk, and a later welcome-back screen replaces it.

`bridge.offline_summary()` returns `{"available": false, "summary": null}` until a welcome-back summary has appeared. After capture, `available` is true and `summary` contains:

- `total`, `generators`, `dividends`, and `autoActions`: exact game amounts represented as decimal strings.
- `secondsAway` and `cappedEarningsSeconds`: elapsed and earnings-capped time supplied by the game.
- `showEarnings`: whether the game marked the earnings section for display.
- `positionChanges`: per-stock results with `stockId`, `isLong`, exact `cashChange` as a decimal string, and `percentChange`.

The API is queryable even if the welcome-back screen appeared before Python connected. This feature only captures data the running game passes to its in-process screen; the bridge does not request extra history or contact the game's backend.

```python
with BridgeClient(token="YOUR_TOKEN") as bridge:
    result = bridge.offline_summary()
    if result["available"]:
        summary = result["summary"]
        print(summary["secondsAway"], summary["total"])
        for change in summary["positionChanges"]:
            print(change["stockId"], change["cashChange"], change["percentChange"])
```

## Discover market status, stocks, and values

`bridge.snapshot()` returns a dictionary assembled from the current in-memory game state:

- `ready`: whether the online market data source is ready.
- `serverTick`: the current market tick.
- `stocks`: stocks visible to the current player and edition. Each record includes `stockId`, `name`, current `price`, `unlocked`, `basePrice`, `priceCap`, `dividendRate`, `maxVolume`, and `availableShares`.
- `positions`: the player's long and short holdings and average prices.
- `cooldowns` and `autoActions`: trade cooldown and auto-action state described below.

The stock list is intentionally scoped to stocks visible to the player; it is not a dump of hidden or unavailable stock definitions. Use each returned `stockId` in later calls rather than inventing identifiers.

```python
with BridgeClient(host="127.0.0.1", port=48721, token="YOUR_TOKEN") as bridge:
    state = bridge.snapshot()
    if not state["ready"]:
        print("Market is not ready")
    else:
        for stock in state["stocks"]:
            print(stock["stockId"], stock["name"], stock["price"], stock["availableShares"])
```

For change notifications, `bridge.subscribe_market(callback)` delivers `market.updated` events containing a new snapshot. Updates are sampled up to four times per second and sent when the snapshot changes.

## Request a leaderboard

`bridge.leaderboard(mode, radius=None, count=None)` asks the running game to use its own in-process leaderboard client. The bridge does not implement leaderboard endpoints or connect to the game's backend itself; in online mode, the game may make its normal online request. These results are requested data, not a dump of leaderboard history already stored in the bridge.

Supported modes match the game's leaderboard screens:

| Mode | Result | Query size |
| --- | --- | --- |
| `current` | Player rankings around the current player | `radius`, default 10, maximum 100 |
| `all_time` | All-time player rankings around the current player | `radius`, default 10, maximum 100 |
| `ipo` | IPO player rankings around the current player | `radius`, default 10, maximum 100 |
| `current_top` | Current top players | `count`, default 100, maximum 100 |
| `clan_net_worth` | Clans ranked by net worth | No limit parameter |
| `clan_player_share` | Clans ranked by player share | No limit parameter |

Player results contain `totalRanked`, `selfRank`, and `entries`; each entry includes `rank`, `steamId`, `displayName`, `clan`, exact `netWorth` as a string, and `ipoCount`. Clan results contain `entries` with `rank`, `clan`, exact `netWorth` as a string, and `playerPercentage`. Both shapes include `mode`, `cached`, and `fetchedAtUnixSeconds`. `fetchedAtUnixSeconds` is when the bridge received the response, not a game server timestamp.

There can be only one leaderboard request in flight, and starting a live request begins a global 30-second cooldown shared by all modes. Identical queries are cached for 30 seconds; cached responses include `cached: true` and do not use another game request. A concurrent query returns `leaderboard_busy`; a different uncached query during the cooldown returns `rate_limited` with `retryAfterMs`. Invalid modes or limits return `invalid_mode`, `invalid_radius`, or `invalid_count`.

This 30-second limit is a bridge safeguard, not a game UI rule. In the inspected demo build, opening the leaderboard and switching its mode each start a refresh, with no client-side cooldown or in-flight lock. The UI uses a refresh token to ignore stale results; that does not cancel requests already sent. The game's server may apply its own limits, which are not established by this client-side behavior.

```python
with BridgeClient(token="YOUR_TOKEN") as bridge:
    around_me = bridge.leaderboard("current", radius=10)
    top_players = bridge.leaderboard("current_top")
    clans = bridge.leaderboard("clan_net_worth")
```

## Read transaction history

`bridge.transaction_history(filter="both", limit=100)` asks the game's in-process transaction-history client for up to 100 recent entries. Filters are `manual`, `both`, and `auto_action`. Each entry includes `id`, `occurredAt`, `stockId`, `side`, `source`, `realizedReturn`, and `realizedPercent`. `realizedReturn` is returned as the game's raw `BigNumberWire` mantissa/exponent string (for example, `{m=8.086,e=8}`), not converted to a decimal string. This is the game's bounded recent-history response, not a complete archive.

The bridge does not connect to the backend itself. The game may make its normal online request through its own client. A successful request from the Transactions screen also seeds the bridge's in-memory cache, so a Python read can reuse it. Otherwise the bridge requests data when its cache is missing or older than the configured freshness period. Results stay in memory only.

`cached` indicates a cached response; `stale` indicates it is older than the configured freshness period. A stale result is returned while a request is already in flight or the bridge request interval has not elapsed. `ageSeconds` reports its age, `fetchedAtUnixSeconds` records when the game response reached the bridge, and `retryAfterMs` is zero unless a refresh must wait. If no cached result exists while requests are being paced, the call raises `BridgeError` with `retry_after_ms` when available.

`CacheSeconds` (default and minimum `60`) and `MinimumRequestIntervalSeconds` (default and minimum `30`) can be increased under `[TransactionHistory]` in `BepInEx/config/screenstocks.bridge.cfg`. Values below those minimums are raised and saved at startup. Restart the game after editing the config. Successful game UI requests seed the same cache and start the bridge cooldown. Opening or changing the Transactions screen can still cause the game UI to request data on its own; the bridge does not suppress those native screen requests.

```python
with BridgeClient(token="YOUR_TOKEN") as bridge:
    history = bridge.transaction_history(filter="both", limit=100)
    for transaction in history["entries"]:
        print(transaction["occurredAt"], transaction["stockId"],
              transaction["side"], transaction["realizedReturn"])
```

## Read and subscribe to graph activity

The game has one active human-activity focus. Set it explicitly with `bridge.set_human_activity_focus(stock_id)`; this changes the game's activity feed without changing the visible graph stock. While active, the game displays a centered, opaque `Activity focus override: STOCK_ID` overlay.

```python
focus = bridge.set_human_activity_focus("TECH")
print(focus)  # {"active": True, "stockId": "TECH"}

page = bridge.human_activity("TECH")
```

Clear the override with `bridge.clear_human_activity_focus()`. The game returns to the activity focus associated with the graph stock selected before the override began. Manually selecting a different graph stock cancels the override and hides the label. The override is global to the running game, not tied to a Python connection; another authenticated client can replace or clear it, and disconnecting does not clear it.

The bridge permits at least five seconds between focus changes it requests. A request that comes too soon raises `BridgeError` with `code == "rate_limited"` and `retry_after_ms` set to the remaining milliseconds. Repeating the active focus is a no-op. Normal graph selection remains immediate.

`bridge.human_activity(stock_id, limit=32, before_tick=None)` retrieves a page of in-memory graph activity for a visible stock. Each sample matches a graph/history tick and contains `sampleTick`, `up`, `down`, `total`, `net`, `upImpact`, `downImpact`, `totalImpact`, and `netImpact`. These are aggregated activity buckets, not individual trade records or player identities. The default and maximum page size is 32; `before_tick` is an exclusive cursor for older samples. For typed access, convert a response with `HumanActivityPage.from_dict(data)`; its `.samples` are `HumanActivity` records.

The bridge reads the activity samples the game currently has in its in-memory market history. Paging walks backward through that available collection; it does not ask the game's server for older history or make the game load more. When `hasMore` is false, you have reached the oldest sample currently exposed by the game, which may not be the beginning of all-time activity. The game controls how many samples it retains, and that retention can vary; the bridge does not guarantee a particular number of samples or a time span. `sampleTick` is the game's history tick, not a wall-clock timestamp, so the API cannot by itself say how many minutes or hours ago a sample occurred.

To receive updates, separately call `subscribe_human_activity(stock_id, callback)` for the currently focused stock. Subscribing immediately sends its current page. The plugin checks subscribed pages up to four times per second and emits `human_activity.updated` when the page changes; each event contains up to 32 latest samples. The game itself observes one activity stock at a time, so another subscribed stock can have empty or stale activity until it is focused. The bridge does not rotate stocks. Older samples that remain in the game's in-memory history are available through the paged query. Under event backpressure, an update may be dropped; query again to resynchronize. Call `unsubscribe_human_activity(stock_id)` to stop updates for one stock. Callbacks run on the Python event worker thread.

```python
def on_event(event):
    if event.get("event") != "human_activity.updated":
        return
    page = event["data"]
    print("activity refresh:", page["stockId"], page["samples"])

bridge.set_human_activity_focus("TECH")
bridge.subscribe_human_activity("TECH", on_event)
# Later:
bridge.unsubscribe_human_activity("TECH")
bridge.clear_human_activity_focus()
```

## Submit individual trades

Each `bridge.trade(action, stock_id, percent=None)` call asks the running game to execute one action using its in-process `StockManager` trade methods. Supported actions are `buy_max`, `buy_percent`, `short_max`, `short_percent`, `sell_max`, `sell_percent`, `cover_max`, `cover_percent`, `close_max`, and `close_percent`. Percentage actions accept values greater than 0 and up to 100. The bridge does not call the game's backend directly; an online game method may contact its server as part of normal game operation.

```python
response = bridge.trade("buy_percent", "STOCK_ID", percent=10)
print(response)  # submitted means accepted for game processing, not completed
```

Trades affect the shared online market. Completion or rejection arrives asynchronously as a `trade.completed` event with the request ID, action, stock ID, status, and, when available, a reason. The bridge checks that a manual trade can start, the stock is visible/unlocked, and the stock is not held before handing the call to the game. The game applies its own affordability, volume, cooldown, and final server checks.

### Multiple submissions and cooldowns

The bridge has no batch-trade endpoint. You can send several individual calls, but each is checked against the game's current manual-trade queue availability and per-stock hold. The bridge does not expose a queue depth or promise that every submitted call will execute.

The game exposes buy and short cooldown end times. A successful buy or short can make later trades subject to that cooldown. The bridge delegates execution to the game's trade methods; it does not bypass their rules. The public API and the checks used before submission do not establish whether a trade that was already queued is rechecked for cooldown when it reaches execution. Therefore, do not assume multiple queued buy/short calls will all succeed. The safest client pattern is to wait for `trade.completed`, refresh `bridge.snapshot()`, and check the relevant cooldown before submitting the next buy or short.

`submitted` only means that the request entered the game's processing path. Use `trade.completed` as the outcome. A failed queue/hold precheck returns an error such as `trade_busy` immediately.

## Read cooldown state

The snapshot's `cooldowns.buy` and `cooldowns.shortTrade` records expose:

- `durationSeconds`: configured cooldown duration, including when idle.
- `availableAtUnixSeconds`: the game-server timestamp when another trade is available.
- `remainingSeconds` and `active`: current live status.
- `serverNowUnixSeconds` and `serverClockSynchronized`: the time basis and sync status.

Auto-action cooldown duration is reported even when no action is on cooldown. Each configured action reports `cooldownUntilUnixSeconds`, `cooldownDurationSeconds`, `cooldownRemainingSeconds`, and `onCooldown`.

## Discover and control auto actions

Call `bridge.auto_actions()` for the current auto-action snapshot. Slot capacity and add availability are queried dynamically from the game manager; do not assume a fixed slot count. The result includes `slotLimit`, `configuredCount`, `canAddAction`, the global `active` state, whether auto actions are `unlocked`, and all configured actions keyed by their game-provided `slotIndex`.

The Python client provides:

- `add_auto_action(stock_id, action_type, condition, target_price, amount_percentage)`
- `update_auto_action(slot_index, stock_id, action_type, condition, target_price, amount_percentage)`
- `remove_auto_action(slot_index)`
- `set_auto_action_enabled(slot_index, enabled)`
- `set_auto_actions_active(active)`

Action types are `Buy`, `Short`, `CloseBuy`, and `CloseShort`; conditions are `Above` and `Below`; percentages are integers from 1 to 100. Add/update require a stock that is visible and unlocked. Use returned `slotIndex` values, not the action's current list position.

Configuring an action does not by itself enable global auto actions. Enabling an action or turning on the global switch can cause the game to submit trades later according to its normal rules. Only activate auto actions when you intend to let the game trade automatically.

## Automatically claim level rewards

The plugin can claim available per-level rewards directly, without Python or a bridge request. The `QualityOfLife.AutoClaimLevelRewards` setting in `BepInEx\config\screenstocks.bridge.cfg` defaults to `true`. The plugin checks the game's claimable level entries and calls the same public `GameManager.ClaimLevel(index)` method used by the game UI, with at most one claim request per second. Entry count and claim eligibility come from the game at runtime; the plugin does not assume a fixed number of levels.

In online mode, the game sends the claim to its server and remains authoritative. Set `AutoClaimLevelRewards` to `false` if you prefer to claim rewards manually. This feature is local plugin behavior: it adds no Python API method and does not use the game's file-based mod API.

## Discover and purchase upgrades

`bridge.upgrades()` reads the game's live upgrade catalog. The catalog is discovered from the current game data, so clients should use the returned `upgradeId` values rather than assuming that every game build has the same list. Each record includes the game's `displayName` and `description`, whether it is hidden, its `currentLevel`, `currentValue`, and whether it is maxed. `hasMaxLevel` distinguishes finite upgrades from unlimited ones. For a finite upgrade, `maxLevel` and `remainingLevels` are numbers; for an unlimited upgrade they are `None` in Python/`null` in JSON. A maxed upgrade has `nextValue` and `nextPrice` set to `None`/`null`. Prices are strings to preserve large game values exactly. `nextValue` applies the next catalog level's change to the current effective value.

```python
with BridgeClient(host="127.0.0.1", port=48721, token="YOUR_TOKEN") as bridge:
    catalog = bridge.upgrades()
    for upgrade in catalog["upgrades"]:
        print(upgrade["upgradeId"], upgrade["description"],
              upgrade["currentLevel"], upgrade["nextValue"], upgrade["nextPrice"])

    result = bridge.purchase_upgrade("AutoActionSlots", quantity=1)
    print(result)  # status is submitted; refresh upgrades() for the observed level
```

`purchase_upgrade(upgrade_id, quantity=1)` asks the running game to process the purchase by calling its in-process `GameManager.PurchaseUpgrades` method. The bridge does not contact the game's backend directly. In online mode, the game method may send the purchase to its server, which remains authoritative. Quantity must be from 1 to 1000. The game's affordability and maximum-level checks still apply. A successful RPC response means the game accepted the purchase request for processing; it can be optimistic, partially fulfilled, or later corrected by the server. `levelBefore` and `levelAfter` are observations around submission, not a completion receipt. Refresh the catalog to see the latest level and maxed state.

The catalog description is the game's own description when provided. These short hints summarize the apparent effect of the built-in upgrade IDs; the game description and live values remain authoritative for a particular build:

| Upgrade ID | Effect hint |
| --- | --- |
| `BuyCooldown` | Reduces the delay between buy trades. |
| `ShortCooldown` | Reduces the delay between short trades. |
| `IncomePerMinute` | Increases passive income per minute. |
| `OfflineEarnings` | Improves the amount earned while away. |
| `DividendGain` | Increases dividend gains. |
| `OfflineHours` | Extends the time window used to accrue offline earnings. |
| `AutoActionSlots` | Increases the number of auto-action slots available. |
| `StockVolume` | Increases the player's allowed stock position volume. |
| `AutoActionCooldown` | Reduces the delay between auto-action executions. |

These hints are based on the upgrade identifiers and UI-facing descriptions found in the current game build. The bridge also returns each catalog record's description so scripts can display the game's own wording. Other bonuses can affect effective values, and the bridge reports `currentValue` through the game's `GetUpgradeValue` method.

Live verification on the demo build confirmed that `upgrades.snapshot` returned a ready catalog with 8 currently defined upgrades, including finite, unlimited, and maxed entries. A one-level `DividendGain` purchase returned `submitted`; a subsequent catalog refresh reported the level increase. The catalog size and available upgrade IDs can vary by game build or player state.

## API methods

| Method | Purpose |
| --- | --- |
| `state.snapshot` | Read current market/player state, cooldowns, and auto actions |
| `state.subscribe` | Subscribe to `market.updated` events |
| `leaderboard.snapshot` | Request one game-supported player or clan leaderboard, subject to cache and cooldown |
| `offline_summary.snapshot` | Read the latest in-memory welcome-back summary |
| `market.human_activity` | Read one page of graph activity for a visible stock |
| `market.human_activity.subscribe` | Subscribe this connection to updates for one visible stock |
| `market.human_activity.unsubscribe` | Stop this connection's updates for one stock |
| `market.human_activity.set_focus` | Override the game's single active activity focus |
| `market.human_activity.clear_focus` | Clear the override and restore the previous graph stock's activity focus |
| `trade.submit` | Submit one allowlisted manual trade |
| `upgrades.snapshot` | Discover upgrade descriptions, levels, values, limits, and next prices |
| `upgrades.purchase` | Ask the running game to process an upgrade purchase |
| `auto_actions.snapshot` | Read auto-action slots and state |
| `auto_actions.add` | Add an action if the game reports a free slot |
| `auto_actions.update` | Replace a configured action's settings |
| `auto_actions.remove` | Remove an action by `slotIndex` |
| `auto_actions.set_enabled` | Enable or disable an action |
| `auto_actions.set_active` | Turn global auto-action execution on or off |

For setup, protocol limits, and troubleshooting, see the [project README](../README.md).
