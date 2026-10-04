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

## Read the graph's activity bars

Use `bridge.human_activity(stock_id, limit=32, before_tick=None)` to retrieve one page for a visible stock. This is an on-demand per-stock call so large histories do not inflate every state snapshot or market update. Each returned sample matches a graph/history tick and contains `sampleTick`, `up`, `down`, `total`, `net`, `upImpact`, `downImpact`, `totalImpact`, and `netImpact`. These are aggregated activity buckets, not individual trade records or player identities.

```python
page = bridge.human_activity("STOCK_ID")
for sample in page["samples"]:
    print(sample["sampleTick"], sample["up"], sample["down"], sample["total"])

if page["hasMore"]:
    older_page = bridge.human_activity("STOCK_ID", before_tick=page["nextBeforeTick"])
```

The default and maximum page size is 32 samples. `before_tick` is an exclusive cursor for older samples. For typed access, convert a response with `HumanActivityPage.from_dict(data)`; its `.samples` are `HumanActivity` records.

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
| `market.human_activity` | Read one page of graph activity for a visible stock |
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
