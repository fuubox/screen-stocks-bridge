# Testing and game compatibility

The bridge reads game objects and calls in-process game methods. Those surfaces can change when Screen Stocks patches. The automated checks are designed to catch known breaks before packaging while keeping ordinary pull-request checks independent of a locally installed game.

## Checks on pull requests

GitHub Actions runs:

- Python client unit tests on Python 3.10, 3.12, and 3.13. They use a local fake JSON-lines server and do not start the game or submit trades.
- .NET unit tests for the Unity-independent core, including protocol JSON, request-parameter parsing, and common request validation.

These tests check the bridge/client boundary. They do not prove that the current game accepts a trade or that a specific UI still behaves as expected.

### Manual-trade queue regression checks

On Windows with the reviewed game installed, run:

```powershell
dotnet build tests/ScreenStocksBridge.Game.Tests/ScreenStocksBridge.Game.Tests.csproj -c Release
python tools/test-game-trade-queue.py
```

The runner embeds the game's installed Mono runtime in an isolated process and
uses its installed Harmony library to apply the production queue patch. It first
reproduces the unpatched cleanup lock, then checks that the patch preserves active
waits and original exceptions, drains faulted actions, handles reentrant previews,
and permits later healthy actions. It also checks the bridge's recovery guard.
It uses synthetic objects and never launches the game or dispatches HTTP/trades.
The ordinary `dotnet test` runner does not execute this project; matching the
game's Mono/Harmony runtime is necessary for this check.

The session-recovery checks additionally run the native broker recovery loop and
API durable-command gate with a controlled JSON-response task. No HTTP request
is sent. They establish that resetting during retry backoff drains the trade gate,
but resetting during an outstanding JSON request only clears ambiguity/completes
its waiter: the request keeps both gates until it finishes. Late responses and
old queued commands are fenced by session generation. An already-queued IPO
barrier survives reset and can later acquire its gate, requiring owner cleanup.
A never-completing response task demonstrates that native session reset alone
cannot guarantee bounded recovery. These are fault-injection tests of the actual
managed methods, not an end-to-end Unity networking test. Unity's integer retry
jitter and clock getters are supplied deterministically by the isolated runner.

The production recovery hooks are then tested against the same native methods:
owned task cancellation drains both gates without manually releasing semaphores,
old IPO acquisitions release and cancel before transferring ownership to their
old caller, and the original ambiguous wait faults with an explicit unknown
outcome. New mutations/barriers are blocked during recovery. Coroutine lifecycle
checks cover abort/dispose exceptions, completion on error, late-resume suppression,
deferred cancellation during iterator advancement, real generic-iterator adapter
binding, and principal isolation. The core tests separately enforce the 30-second
ambiguity window and a single 15-second recovery budget, fresh-state validation,
and no unlocking after timeout. Actual UnityWebRequest abort/disposal and a full
live auth/scene reconnect remain live-game smoke checks; the isolated harness
uses controlled transport tasks and abort callbacks.

The October 28, 2026 evening review targets Steam build 26281852 (installed
5:19 p.m. Toronto), SHA-256 `7ce68615fbf5db153b12976208d49d73f54d75fec489ed80778ce95d0283329c`,
MVID `19110632-9ec2-462b-bc28-0c400b4104b5`. The previous reviewed assembly
was `ac975fe3...` / `da6422d0-3325-4c79-bfe4-ef9f5955c92b`.

Direct Cecil instruction/exception-handler comparison of the baseline and candidate
found no body changes in RunQueuedManualTradeAsync, its generated MoveNext,
manual admission, ambiguity wait/reset, dispatch/gate loops, the three recovery
barrier clients, API SendCoroutine, or auth session reset. The 5e41804 queue
handoff fix remains necessary: native cleanup can throw before completing the
tail. The installed-Mono harness reproduces that failure and verifies the fix.

SellAsync/CoverAsync on ITradeBroker and both implementations now take a fifth
Boolean, closeAll. StockManager's unchanged public entry signatures still serve
TradeService; their core state machines compute closeAll as actual >= owned/short
position and pass it through the changed closures. RemoteTradeBroker forwards it
to the existing SendTrade close-all slot (previously false); LocalTradeBroker
ignores it. No bridge broker call or recovery hook targets those changed signatures.
An offline dispatch interception tests both flag values for both remote methods.
The manifest now covers all six broker signatures.

Other changed behavior includes local position-based GetAvailableShares, auto-action
prediction/ordering and close-all handling, clearing auto-action predictions in
CommitProgressResetBarrier, and remote player refresh. These changes do not justify
rewriting native trade semantics. Recovery retains its ambiguity window, single
budget, principal/generation fencing, owned-request drain and fresh-state requirement.
Both queue and recovery hooks, including the API coroutine adapter, require the
reviewed MVID and full assembly hash before installation; unknown binaries fail closed.

Offline validation passed the compatibility manifest check, Release plugin/game-test
build, and installed Mono/Harmony queue, recovery and request-ownership regressions.
All 63 core and 10 Python client tests also passed (local fake-server sockets
required permission beyond the restricted runner; .NET 10 runtime roll-forward
was used). NuGet vulnerability-feed lookup was unavailable. No game process was launched or
market action submitted. Live validation remains pending: after deliberate deployment
and restart, confirm both protection hooks load, read-only snapshots agree with the
UI, and observe real Unity request abort/disposal and reconnect in a controlled test.
Online close-all acceptance and auto-action/IPO behavior are not proven by this harness.
The ignored candidate has not been promoted over the prior baseline.

### October 29, 2026 game update: Steam build 26298415

The installed `Assembly-CSharp.dll` has MVID
`20f0cbb7-bcb4-4ccf-9354-ec2ae5d91b51` and SHA-256
`6f396487493579564e6739daea7413295eefbc559ccf3690c68fd602468cf3a1`.
BepInEx, Harmony, Mono.Cecil, and the Unity managed references are unchanged
from build 26281852.

`StockManager.RunQueuedManualTradeAsync` and `StartManualTrade` now take four
arguments; the last is `ManualTradeShape`, replacing the previous predicate and
two close-volume arguments. The native queue cap increased from 3 to 20 and the
queue now tracks per-stock admission tails and unshown close/open state. The
generated queue state machine changed substantially, but it still publishes
`_manualActionTail` before invoking the preview callback. Its cleanup clears the
pending registry before signaling the action completion source, so a cleanup
exception can still leave that tail unresolved. The bridge's tail-settling
Harmony wrapper remains necessary and now validates the four-argument contract.

The reconnect, ambiguous-trade wait/reset, recovery-barrier, API request-session,
and authentication session-boundary methods have no instruction or exception
handler changes after normalizing generated state-machine names. The API
`SendCoroutine` iterator name and fields used by the cancellation adapter remain
unchanged. This is offline compatibility review only; live Unity networking,
reconnect behavior, and market acceptance remain unverified.

## Check a game patch

The trusted Windows runner must have the full Screen Stocks release and BepInEx installed. Set the repository Actions variable `SCREENSTOCKS_GAME_DIR` to that installation; the fallback path is `C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks`. A push to `main` or a manual run of the **Continuous integration** workflow checks the installed binaries against `tools/game-compatibility.json`, then builds the plugin against the installed assemblies. Pull requests do not execute repository code on this self-hosted runner.

The demo fingerprint has been retired. The first full-release review found the `Assembly-CSharp.dll` and referenced Unity assemblies byte-for-byte identical to the pre-release baseline, with no managed API signature changes. `tools/game-compatibility.json` records the full-release fingerprint and the installed BepInEx/Harmony versions. A signature match confirms the checked binary surface; use the in-game smoke checks below for runtime behavior.

For the first full-release review, this workspace already has a pre-release local baseline. Run `tools/compare-game-reference.ps1 -Mode Compare` after a game patch to create a candidate and member-level diff. Review changed contracts and relevant behavior before updating the compatibility manifest. Build and run the in-game smoke checks against the full game before promoting a reviewed candidate with `-Mode Promote`. Update the repository's `SCREENSTOCKS_GAME_DIR` variable to the full-game install path as well.

When Steam updates the game, review the patch before refreshing the manifest:

1. Before the first patch review on a machine, initialize a local reference copy with `tools/compare-game-reference.ps1 -Mode Initialize`. This copies only the assemblies used as build/compatibility references into `.local/game-reference/baseline` and extracts readable type/member inventories for every copied managed assembly. `.local` is Git-ignored; these game and BepInEx binaries are not committed, uploaded, or included in releases.
2. After Steam updates the game, run `tools/compare-game-reference.ps1 -Mode Compare`. It copies the installed references to `.local/game-reference/candidate`, reports changed assembly hashes/versions, and writes sorted member-level diffs to `.local/game-reference/latest-diff.txt`. The report identifies additions/removals of types, fields, methods, properties, and events across the game, Unity, BepInEx, and Harmony references. A binary hash change alone does not establish that a bridge contract changed.
3. Run `tools/check-game-compatibility.ps1`. A changed Assembly-CSharp hash, MVID, BepInEx/Harmony version, required method signature, or reflected field fails the check.
4. Compare data authority and behavior, not only signatures. For each bridge field, determine whether the game's current UI reads a local ScriptableObject, a cached remote DTO, a predicted/optimistic value, or a live accessor that combines several sources. Check whether the UI path and bridge path use the same source and level/index convention. A signature can remain unchanged while its source of truth or clamping behavior changes.
5. Specifically inspect upgrade data after a patch: compare `GameManager.GetUpgradeLevel`, `GetUpgradeValue`, `IsUpgradeMaxed`, and `GetUpgradePrice` with `RemoteUpgradesClient` and `UpgradeViewDto` fields (`currentLevel`, caps, values, next price, and `maxed`). Do not calculate remote caps or next-level changes from the local `PlayerUpgradesSO` if the game now has remote upgrade data. Verify any such mismatch against the in-game UI before refreshing the compatibility manifest.
6. Inspect the updated game assemblies and compare every reported type/member used by the plugin, especially Harmony targets, reflected private members, market data access, and action methods.
7. Run the in-game smoke checks below against the updated game. Exercise relevant features only; avoid routine online trades during CI.
8. Update `tools/game-compatibility.json` with the new observed fingerprint and signatures only after review. The fingerprint is a review gate, not proof that every game behavior is unchanged.
9. Run the local checks, build against the updated installation, and push the reviewed manifest/code changes. The `main` workflow then confirms that the checked-in fingerprint matches the runner's installation.

After reviewing a patch and refreshing the tracked compatibility manifest, promote the candidate so it becomes the local reference for the next patch: `tools/compare-game-reference.ps1 -Mode Promote`. Promotion replaces only the ignored local baseline. Keep the tracked compatibility manifest limited to the reviewed fingerprint and required contract signatures; do not add raw game or BepInEx DLLs to Git.

If the game changes in a way that breaks the bridge, keep the compatibility check failing until the code is adapted and reviewed. Do not weaken it by replacing the expected hash alone.

## In-game smoke checks

Use a development installation with the game open. These checks are manual because they require the real game state and should not submit market actions automatically:

- Plugin loads and binds only to `127.0.0.1` on the configured port.
- `state.snapshot` reports readiness and visible stock IDs/values on the market screen.
- `news.subscribe` succeeds; while subscribed, verify an ordinary `market` ticker event when one naturally renders. Verify a `scheduled_price` event only if the game naturally renders one during the check; it can be rare or time-dependent, so do not force backend requests or delay routine smoke tests waiting for it. Confirm event text and structured stock ID come from the rendered item, then unsubscribe and confirm that connection stops receiving ticker events. A later subscription does not replay earlier items.
- `market.human_activity` reads current graph activity; the focus override changes the observed stock, shows its overlay, respects the five-second change limit, and restores the previous focus when cleared.
- `offline_summary.snapshot` returns unavailable before a welcome-back summary has been captured, and returns the captured values after a session displays it. Verify the auto-close setting separately when testing the Harmony hook.
- `upgrades.snapshot` reports available/maxed upgrades and agrees with the game UI on the current level, cap, current effective value, and next-level change; only submit a purchase when intentionally testing against the running game.
- `auto_actions.snapshot` discovers current slots and cooldowns. Mutating actions should be tested intentionally and not as a routine CI step.
- Submit at most one deliberate trade to verify request acceptance and completion reporting, then confirm the game's visible market/player state. Do not use automated checks to send trades to an online shared market.

Record the game build/fingerprint, feature tested, and result in the change or release notes when a game patch required compatibility work.
