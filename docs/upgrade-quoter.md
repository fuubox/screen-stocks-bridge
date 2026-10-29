# Upgrade quoter

The upgrade quoter previews the cost and effective value of an upgrade scenario
using state already held by the running game. It does not spend money, submit a
purchase, or refresh data from the backend. Use it to compare quantities or find
how many levels fit a budget before deciding whether to buy.

This API requires a plugin and Python client containing `upgrades.quote` and
`quote_upgrade()`. It is available in release v0.14.5 and later; install matching
plugin and Python client versions.

## Quote five additional levels

Discover IDs with `upgrades()` instead of relying on a fixed upgrade list:

```python
from decimal import Decimal
from screenstocks_bridge import BridgeClient

with BridgeClient(token="YOUR_TOKEN") as bridge:
    for upgrade in bridge.upgrades()["upgrades"]:
        print(upgrade["upgradeId"], upgrade["currentLevel"], upgrade["description"])

    quote = bridge.quote_upgrade("DividendGain", quantity=5)
    print("Levels:", quote["levelBefore"], "->", quote["levelAfter"])
    print("Additional levels quoted:", quote["quantity"])
    print("Total cost:", Decimal(quote["totalCost"]))
    print("Effective value:", quote["currentValue"], "->", quote["resultingValue"])
    print("Affordable:", quote["affordable"])
    print("Stopped because:", quote["stopReason"])
```

`quantity=5` means **five more levels**. If the current level is 10, it quotes
levels 10 to 15. `target_level=5` instead means an absolute destination of level
5; a player already at level 10 receives a zero-level quote for that target.

An upgrade cap can reduce the quantity. If only two levels remain, requesting
five returns `quantity=2`, `capReached=true`, and `stopReason="maxed"`.

## Select the scenario

Supply exactly one selector. Omitting all selectors quotes one additional level.
`max_levels` is a separate optional calculation bound usable with any selector.

| Selector | Example | What it resolves |
| --- | --- | --- |
| `quantity` | `quantity=5` | Cost and value after five additional levels |
| `budget` | `budget="1000000.25"` | Largest additional quantity within that money budget |
| `cash_fraction` | `cash_fraction=0.5` | Largest additional quantity within half the observed cash |
| `cash_fraction` | `cash_fraction=1` | Largest affordable additional quantity using all observed cash |
| `target_level` | `target_level=100` | Quantity and cost to reach absolute level 100 |
| `target_value` | `target_value=2.5` | First level reaching the requested effective value |

The installed game's upgrade buttons use Shift-click for maximum affordable
and Ctrl-click for half-cash; Ctrl takes priority when both are held. The cash
fraction modes express those budgets without simulating keyboard input.

### Budget and cash-fraction quotes

```python
half_cash = bridge.quote_upgrade("DividendGain", cash_fraction=0.5)
all_cash = bridge.quote_upgrade("DividendGain", cash_fraction=1)
fixed_budget = bridge.quote_upgrade("DividendGain", budget="1000000.25")
```

Budget is a **string**, not a Python float. Accepted strings contain digits and
optionally a decimal point followed by one to six digits. Signs, separators,
suffixes, and scientific notation are not accepted. The maximum string length
is 256 characters. `budget="0"` is valid and can include zero-cost levels.

Cash fractions must be finite numbers from `0.000001` to `1`. The fraction is
rounded down to millionths, then the budget is calculated using the game's
money arithmetic and rounded down to the game's money precision. Inspect the
returned `budget` for the resolved amount.

An explicit budget can exceed available cash. In that case the resolved plan
can be unaffordable. Budget modes stop before the next level would exceed the
budget; they also respect caps and the calculation bound.

### Target quotes

```python
level_target = bridge.quote_upgrade("DividendGain", target_level=100)
value_target = bridge.quote_upgrade("DividendGain", target_value=2.5)
```

An increasing-value upgrade reaches a target when its effective value is **at
least** the requested value. A decreasing-value upgrade, such as a cooldown
reduction, reaches it when the effective value is **at most** the requested
value. Values use the raw units reported by the catalog; inspect the upgrade's
description and `currentValue` rather than assuming percentage or display units.

Already-met targets cost zero and return `targetReached=true`. A target beyond
the cap returns a partial quote with `capReached=true` and `targetReached=false`.
For a constant-value upgrade, only an equal value target is supported; another
value returns `unreachable_target`.

Quantity and target modes calculate the scenario even if cash cannot cover it.
They do not stop at the player's cash balance: `affordable` becomes false and
`remainingCash` can be negative. Use a budget mode for an affordable quantity.

## Read the response

The Python method returns the result dictionary directly. This illustrative
response assumes three additional levels costing 100, 200, and 400, with an
effective value increase of 0.125 per level. It is not live game data:

```json
{
  "upgradeId": "DividendGain",
  "mode": "quantity",
  "quantity": 3,
  "levelBefore": 10,
  "levelAfter": 13,
  "currentValue": 1,
  "resultingValue": 1.375,
  "totalCost": "700",
  "cashBefore": "1000",
  "remainingCash": "300",
  "budget": null,
  "affordable": true,
  "capReached": false,
  "truncated": false,
  "targetReached": null,
  "calculationLimit": 5000,
  "stopReason": "quantity_reached",
  "valueSource": "remote_catalog"
}
```

| Field | Meaning |
| --- | --- |
| `upgradeId`, `mode` | Resolved catalog ID and quote mode |
| `quantity` | Number of additional levels included in this quote |
| `levelBefore`, `levelAfter` | Observed starting level and projected ending level |
| `currentValue`, `resultingValue` | Observed and projected effective upgrade values, as JSON numbers |
| `totalCost` | Sum of the included levels' prices, as a decimal string |
| `cashBefore`, `remainingCash` | Observed cash and cash minus total cost, as decimal strings |
| `budget` | Resolved decimal-string budget for budget/cash-fraction modes, otherwise null |
| `affordable` | Whether observed cash covers the entire quoted cost |
| `capReached` | Whether the projected ending level reaches the game's upgrade cap |
| `targetReached` | Whether a target was reached; null outside target modes |
| `truncated` | Whether the calculation bound stopped the quote |
| `calculationLimit` | Maximum additional levels evaluated for this request |
| `stopReason` | Reason evaluation stopped, described below |
| `valueSource` | `remote_catalog` when the remote formula is available, otherwise `local_catalog` |

Money strings preserve the game's large amounts and six-digit fractional
precision. Use `Decimal` for client-side money comparisons rather than `float`.
Effective values retain the game's floating-point precision.

| `stopReason` | Interpretation |
| --- | --- |
| `quantity_reached` | All requested additional levels were included |
| `target_reached` | The target level or effective value was reached, possibly already at the start |
| `budget_exhausted` | The next level would exceed the selected budget |
| `maxed` | The upgrade cap prevented adding another level |
| `calculation_limit` | The work bound prevented further evaluation; `truncated` is true |

A quote can reach its requested quantity and cap simultaneously:
`stopReason="quantity_reached"` and `capReached=true` are consistent. An already
maxed upgrade can return zero quantity and zero cost.

## Limits and accuracy

`max_levels` defaults to 5,000 and accepts integer values from 1 to 5,000. It
limits **additional levels evaluated**, not the absolute target level:

```python
quote = bridge.quote_upgrade("DividendGain", cash_fraction=1, max_levels=250)
if quote["truncated"]:
    print("This is only a calculated prefix, not the full maximum affordable quantity.")
```

Requested quantities can range from 1 to 2,147,483,647, but the calculation
still evaluates at most `max_levels`. Absolute target levels range from 0 to
2,147,483,647; target values must be finite numbers. Repeating the same quote
does not advance the player's level or continue a truncated calculation.

Prices are summed using the game's `GetUpgradePriceAtLevel` method. Projected
values use the remote catalog's level formula when available, otherwise the
local catalog, keeping the observed level-reward and IPO bonuses fixed. The
quote does not forecast changes to bonuses, income, or market conditions and
does not calculate ROI or payback rankings.

A quote describes currently observed game state. Pending purchases may be
reflected optimistically, and server corrections or later cash changes can
invalidate the preview. Very large levels can exceed the game's pricing math;
such failures return `quote_unavailable` rather than a purchase commitment.

## Purchasing is a separate request

Quoting never triggers `purchase_upgrade()`. The existing purchase API still
accepts **1–1,000 additional levels per request**, even though a quote can
evaluate up to 5,000. No automatic splitting or purchase loop is performed.

For a deliberate five-level purchase, review the quote first and then explicitly
submit a purchase. The following block spends in-game money when its condition
is met:

```python
quote = bridge.quote_upgrade("DividendGain", quantity=5)
if quote["quantity"] == 5 and quote["affordable"] and not quote["truncated"]:
    submitted = bridge.purchase_upgrade("DividendGain", quantity=5)
    print(submitted)  # Submission, not a completion receipt
    print(bridge.upgrades())  # Observe the latest level; server settlement may follow
```

The server remains authoritative. A quote does not reserve cash or guarantee
that a later purchase will complete at the quoted quantity or price.

## Raw JSON-lines RPC

Send the usual authenticated request envelope using method `upgrades.quote`:

```json
{"id":"quote-5","method":"upgrades.quote","token":"YOUR_TOKEN","params":{"upgradeId":"DividendGain","mode":"quantity","quantity":5,"maxLevels":5000}}
```

For a variable quantity based on half the observed cash:

```json
{"id":"quote-half","method":"upgrades.quote","token":"YOUR_TOKEN","params":{"upgradeId":"DividendGain","mode":"cash_fraction","cashFraction":0.5}}
```

| Mode | Selector field in `params` |
| --- | --- |
| `quantity` | `quantity` |
| `budget` | `budget` (decimal string) |
| `cash_fraction` | `cashFraction` |
| `target_level` | `targetLevel` |
| `target_value` | `targetValue` |

`upgradeId` is required; `maxLevels` is optional. Omitted mode selects quantity,
and omitted quantity defaults to one. The mode chooses the selector used by the
RPC; the Python helper rejects multiple selectors instead of choosing one.
Success uses the normal `ok=true` envelope with the quote dictionary in `result`.

## Errors

The Python helper raises `ValueError` for conflicting selectors and invalid
client-side types or bounds before sending a request. Remote errors raise
`BridgeError`; inspect its `code` and message.

| Error code | Cause |
| --- | --- |
| `not_ready` | The game's upgrade catalog is not initialized |
| `invalid_upgrade` | Upgrade ID is empty, too long, or not a recognized stat ID |
| `upgrade_not_found` | The recognized stat is absent from the current catalog |
| `invalid_mode` | Unknown quote mode |
| `invalid_quantity` | Quantity is not a positive integer within the RPC's integer representation |
| `invalid_budget` | Invalid decimal budget or cash fraction |
| `invalid_target` | Negative target level or non-finite target value |
| `invalid_limit` | Calculation limit is outside 1–5,000 |
| `unreachable_target` | A constant-value upgrade cannot attain the requested different value |
| `quote_unavailable` | The game's price/value calculation or money parsing failed |

Authentication and malformed-frame errors follow the existing
[bridge protocol](bridge-usage.md). This feature's calculator and client tests,
plugin build, and installed-game compatibility checks have passed; live quote
verification is still pending.
