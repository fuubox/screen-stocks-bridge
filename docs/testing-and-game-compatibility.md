# Testing and game compatibility

The bridge reads game objects and calls in-process game methods. Those surfaces can change when Screen Stocks patches. The automated checks are designed to catch known breaks before packaging while keeping ordinary pull-request checks independent of a locally installed game.

## Checks on pull requests

GitHub Actions runs:

- Python client unit tests on Python 3.10, 3.12, and 3.13. They use a local fake JSON-lines server and do not start the game or submit trades.
- .NET unit tests for the Unity-independent core, including protocol JSON, request-parameter parsing, and common request validation.

These tests check the bridge/client boundary. They do not prove that the current game accepts a trade or that a specific UI still behaves as expected.

## Check a game patch

The trusted Windows runner has Screen Stocks Demo and BepInEx installed. A push to `main` or a manual run of the **Continuous integration** workflow checks the installed binaries against `tools/game-compatibility.json`, then builds the plugin against the installed assemblies. Pull requests do not execute repository code on this self-hosted runner.

When Steam updates the game, review the patch before refreshing the manifest:

1. Run `tools/check-game-compatibility.ps1`. A changed Assembly-CSharp hash, MVID, BepInEx/Harmony version, required method signature, or reflected field fails the check.
2. Inspect the updated game assemblies and compare every reported type/member used by the plugin, especially Harmony targets, reflected private members, market data access, and action methods.
3. Run the in-game smoke checks below against the updated game. Exercise relevant features only; avoid routine online trades during CI.
4. Update `tools/game-compatibility.json` with the new observed fingerprint and signatures only after review. The fingerprint is a review gate, not proof that every game behavior is unchanged.
5. Run the local checks, build against the updated installation, and push the reviewed manifest/code changes. The `main` workflow then confirms that the checked-in fingerprint matches the runner's installation.

If the game changes in a way that breaks the bridge, keep the compatibility check failing until the code is adapted and reviewed. Do not weaken it by replacing the expected hash alone.

## In-game smoke checks

Use a development installation with the game open. These checks are manual because they require the real game state and should not submit market actions automatically:

- Plugin loads and binds only to `127.0.0.1` on the configured port.
- `state.snapshot` reports readiness and visible stock IDs/values on the market screen.
- `market.human_activity` reads current graph activity; the focus override changes the observed stock, shows its overlay, respects the five-second change limit, and restores the previous focus when cleared.
- `offline_summary.snapshot` returns unavailable before a welcome-back summary has been captured, and returns the captured values after a session displays it. Verify the auto-close setting separately when testing the Harmony hook.
- `upgrades.snapshot` reports available/maxed upgrades; only submit a purchase when intentionally testing against the running game.
- `auto_actions.snapshot` discovers current slots and cooldowns. Mutating actions should be tested intentionally and not as a routine CI step.
- Submit at most one deliberate trade to verify request acceptance and completion reporting, then confirm the game's visible market/player state. Do not use automated checks to send trades to an online shared market.

Record the game build/fingerprint, feature tested, and result in the change or release notes when a game patch required compatibility work.
