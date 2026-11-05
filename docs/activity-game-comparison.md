# Activity: game, bridge, and desk

This records the pre-v2 comparison that motivated the redesign. For the replacement
contract, see [Activity v2](activity-v2.md).

Reviewed 2026-11-05 against the installed `Assembly-CSharp.dll`, SHA-256
`c7f7be7089891d1740eb7b97b8a5c83d43c7cc73e82c468a7ad992700093a7d8`,
which matches the repository compatibility manifest. This is an offline review
of the actual managed methods and local bridge/desk code. It does not establish
how the remote server computes impact from trades.

## Comparison

| Concern | Game | Bridge / trading desk |
| --- | --- | --- |
| Focus | One global focused stock. Graph navigation changes it. Changing focus clears the client's focused data and changes the server subscription. | Bridge pins an explicit override, reapplying it after graph navigation. Clear restores the focus saved when the override began. Desk adds ownership, persistence, and history capture. |
| Incoming updates | Full responses replace retained buckets. Realtime deltas replace matching bucket IDs, prune expired buckets, and invalidate alignment. | Bridge polls the resulting page every 0.25 seconds and publishes changed latest pages of at most 32 samples. These are snapshots, not incremental trade events. |
| Alignment | Allocates a zero-filled array matching price-history ticks. Places each valid bucket once, near its interval midpoint when a history tick falls inside; otherwise uses the preceding history position. Buckets whose end is outside the retained tick range are skipped. Several buckets can land on one sample. | Bridge returns these aligned samples directly, paired with history ticks. Original bucket ID, interval width, retention boundary, and placement provenance are absent. |
| History | Holds a bounded focused bucket collection and price history. Nonfocused stock reads return no aligned activity. | Bridge pagination walks the current aligned collection. Desk imports it and retains observations locally; local history can outlive the game's current collection. |
| Game histogram | Sums adjacent samples into width-dependent groups. Positive-impact heights scale to the largest visible grouped total impact. Color uses grouped net impact divided by total impact. Counts provide the fallback when no visible impact is positive. | Desk `focus_activity_minutes` keeps the latest sample and a separate peak sample per time bucket. This is a descriptive view with different aggregation. |
| Scale reference | Receives `fullScaleImpact`; invalid/nonpositive values fall back to `0.2`. The positive-impact histogram uses the visible grouped maximum instead. | Bridge exposes the reference unchanged. Desk computes `impact / fullScaleImpact * 100`; this measures percent of that reference, not percent of an actual in-game bar's height. |
| Freshness | Aligned sample positions follow price history, including zero-filled positions without placed activity buckets. | Desk checks page receipt time and latest sample tick. Those establish fresh bridge observations and recent price alignment, but cannot establish a fresh server activity response. |

## What we already handle correctly

- The bridge uses the game's activity values and alignment rather than rebuilding them.
- Desk page merging replaces rows by stock/tick, avoiding double counting repeated snapshots.
- Desk history distinguishes retained imports from live page observations, and avoids attaching the current page scale to older imported rows as if it were their historical scale.
- The desk's latest/peak view is explicitly descriptive. It can be useful without reproducing the game's histogram.

## Changes worth making

1. **Correct the meaning of chart scale.** The bridge guide previously described
   `fullScaleImpact` as the normal histogram denominator; this review corrects it.
   Desk labels such as "% chart scale" should say "% server reference" unless
   they actually reproduce the game's visible grouping and normalization.
   For example, visible group impacts of `0.01` and `0.02` draw at approximately
   50% and 100% height before pixel adjustments, even if the reference is `0.2`.
   Reference percentages for those same values are 5% and 10%.
2. **Separate observation freshness from source freshness.** Add activity-source
   metadata at the bridge boundary before claiming that a recent aligned zero
   means a current quiet market. Useful metadata would include focused stock,
   bucket interval and retained coverage, and when an actual response/delta was
   received. The present aligned API cannot distinguish every zero's origin.
3. **Choose chart intent explicitly.** Keep latest/peak summaries if the goal is
   descriptive analysis. Reproducing the game's display requires summing groups,
   computing the visible maximum, and coloring by grouped net/total. Minute or
   second peaks are different quantities and should retain those labels.
4. **Review revision capture.** Only the latest 32 aligned rows are pushed.
   Revisions outside that window are available through queries but do not by
   themselves change the pushed page. Initial pagination is also a sequence of
   reads of mutable history, not an atomic snapshot. Persistent capture should
   document this limit or add reconciliation for older retained positions.
5. **Update the desk's focus warning.** Its current message says game graph
   selection can cancel the desk override. This bridge instead reasserts the
   override during its update loop. Navigation can still transiently switch and
   clear focused game data before reassertion; the warning should describe the
   actual uncertainty rather than assert cancellation.

## Evidence locations

- Installed game methods: `HumanActivityClient.SetFocus`, `Apply`,
  `HandleRealtimeActivityDelta`, `GetAligned`; `HumanActivityAligner.Align` and
  `FindPlacementSample`; `UILineGraph.DrawActivityHistogram`,
  `GetActivityGroupImpact`, and `GetActivityGroupCount`.
- Bridge: `plugin/ScreenStocksBridge/HumanActivityService.cs`,
  `HumanActivityFocusService.cs`, and the subscription/update paths in `Plugin.cs`.
- Sibling desk checkout: `screen_stocks_desk/focus_activity.py`,
  `activity_learning.py`, `base_ringing/activity.py`, `hot_history.py`, and
  `service.py` (`_merge_activity_page`, `_fetch_human_activity`, and the
  `human_activity.updated` handler).

No runtime code, focus settings, or trading settings were changed by this review.
