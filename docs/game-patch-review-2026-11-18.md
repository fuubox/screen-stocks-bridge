# Game patch review — 2026-11-18

Reviewed installed Steam build **26701870** against bridge **0.17.1**.

## Findings before the compatibility update

1. **Automatic trade recovery is disabled on the new build.**
   `ReviewedGameBuild.Require()` still admits only the previous MVID/hash.
   `OwnedApiRequests.Install()` rejects the new assembly before the recovery
   hooks finish installing. `Plugin` catches this, removes those hooks, and
   continues with recovery unavailable. The installed BepInEx log independently
   confirms: `Automatic trade recovery is unavailable: Trade protection requires
   a reviewed game build.` This also prevents installing the request-ownership
   protection in that installation attempt. Refreshing the reviewed-build gate
   requires a compatibility update; no production gate was changed in this review.

2. **The IPO snapshot's peak can disagree with the game's eligibility basis.**
   Native `CanIPO()` calls `GetRunPeakNetWorthForIpo()`. In the full remote game,
   that accessor returns the greater of the stored run peak and current net worth
   minus predicted payout cash, clamped to zero. The bridge correctly delegates
   eligibility to `CanIPO()`, but `IpoService.CreateSnapshot()` still reports
   `game.Data.roundPeakNetWorth`. Consequently `eligible` can be true while the
   reported peak remains below the requirement. The peak should follow the same
   accessor as the native eligibility/UI path. This is a static authority mismatch;
   no live IPO or trade was submitted to reproduce it.

## Resolution in bridge 0.17.2

The production reviewed-build guard and compatibility manifest now admit the
exact candidate MVID/hash below. Unknown builds continue to fail closed. IPO
snapshots now use `GetRunPeakNetWorthForIpo()`, and the manifest checks that
accessor's signature. The new offline regression reproduced the old mismatch
(`eligible: true`, stored peak `100`, requirement `1000`) and verifies the bridge
now reports the controlled native effective peak for both eligible and ineligible
cases. All patch installers are exercised by the tracked native regression runner.

The full native suite is rerun against the production source, rather than relying
on the earlier isolated review copy. Runtime Unity/network smoke checks remain
outside the offline verification; no game DLL installation or live commands are
part of this source/release update.

## Patch checks

In an isolated process using the installed game's Mono runtime:

- Auto-action marker correction and purchase-marker vertical spacing install.
  Native draw/reconciliation tests pass, including scale, edges, timing anchors,
  delayed confirmations, slot ownership, and restoring stored marker values.
- Activity v2 and market stream hooks install and their native application,
  alignment, replay, fallback, and payout tests pass.
- Auto-action unlock and level-data refresh, toast capture and compact styling,
  news ticker, offline summary, net-worth history, and transaction history Harmony
  installers all pass.
- Full queue, recovery, reset/barrier, and owned-request regressions pass **in the
  isolated review copy with the candidate fingerprint admitted**. The ordinary
  source/build correctly refuses the candidate at its reviewed-build gate.
- Building the current source against the updated game succeeds with zero
  warnings and zero errors. Every signature/field in the compatibility manifest
  matches when the candidate fingerprint is substituted in the review copy.

The changed `StockManager.SendManualLegAsync` state-machine body adds an accepted,
settled-trade leaderboard self-rank refresh after its existing cleanup logic.
The installed-Mono cleanup, exception, reentrancy and ownership checks still pass.
The upgrade level/value/maxed/price accessors retain their remote-data and
prediction paths in the available saved method-body comparison.

## Evidence and limits

Candidate `Assembly-CSharp.dll`:

- MVID: `147c912a-2142-488b-9aa1-ba219f725d25`
- SHA-256: `55be35ae6b44b3d45542ebccc43c3dfa4db69d6b5c262ee34ba5bb624b04ee3d`
- Assembly version: `0.0.0.0`

Unity references, BepInEx 5.4.23.6, Harmony 2.9.0.0 and Mono.Cecil are unchanged
against the saved reference snapshot. The saved full binary baseline dates to
October 28, so its broad surface diff includes earlier changes, not exclusively
this patch. Later saved IL dumps, including November 5 marker/activity reviews,
were used where available to narrow behavior comparisons.

Reproducible local evidence is under the ignored `.local` directory:

- `game-reference/latest-diff.txt`: assembly and member-surface comparison.
- `game-patch-20261118-il.txt` and `game-patch-20261118-full-method-diff.txt`:
  candidate method bodies and comparisons to saved IL dumps.
- `game-patch-20261118-native.log`: ordinary-build tests, ending at the expected
  reviewed-build refusal.
- `game-patch-20261118-all-patches.log`: all Harmony installers and full native
  regression suite in the isolated review copy.
- `game-patch-review-20261118/`: ignored source copy; only this copy's compatibility
  gate/manifest admit the candidate, with a test-only installer smoke harness.

The initial review changed no production fingerprints. The 0.17.2 resolution
updates the tracked manifest/runtime guard as described above. Installed
game/plugin DLLs, the saved binary baseline, configuration, saves, and live game
state remain untouched. No game was launched and
no bridge connection, remote request, trade, upgrade, claim or IPO was submitted.
Offline installer/behavior checks cannot prove rendered Unity UI behavior or
live server acceptance; those remain smoke checks after a compatibility update.
