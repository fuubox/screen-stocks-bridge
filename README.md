# Screen Stocks Python Bridge

This project builds a BepInEx plugin and a small Python client for an in-memory API to the running Screen Stocks demo. The plugin owns a TCP listener bound only to `127.0.0.1`; Python can request live state, subscribe to changed snapshots, submit allowlisted trades, and inspect or control the game's auto actions.

The bridge does not enable or use the game's official `modSupport` file API. It creates no game `mods` folder and writes no market exports, command files, API snapshots, request files, or trade results. Normal game saves remain enabled. BepInEx continues writing its own plugin config and logs; the bridge stores its access token in that config and prints a newly generated token once to the BepInEx log.

## Install a release

Install the game and a compatible BepInEx 5 release first. Download the latest `ScreenStocksBridge-v<version>.zip` from [GitHub Releases](https://github.com/fuubox/screen-stocks-bridge/releases), close the game, and extract the ZIP into the Screen Stocks installation folder. The archive already contains the plugin directory structure. The resulting DLL path is:

```text
BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.dll
```

The ZIP also includes the project's MIT-0 license beside the DLL. It does not bundle or install BepInEx. Keep the game's `modSupport` setting off. Restart the game after installation or replacing the DLL so BepInEx loads it.

## Build the plugin from source

Install the .NET SDK and build from the repository root in PowerShell:

```powershell
dotnet build .\plugin\ScreenStocksBridge\ScreenStocksBridge.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks Demo"
```

The plugin references the installed BepInEx and game assemblies without copying them into the output. The built plugin is:

```text
plugin\ScreenStocksBridge\bin\Release\netstandard2.1\ScreenStocksBridge.dll
```

For a manual install, close the game and copy the DLL into:

```text
C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks Demo\BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.dll
```

Keep the game's `modSupport` setting off. Restart the game after replacing the plugin DLL.

## Publish a release

The release version must match both the `<Version>` value in `plugin/ScreenStocksBridge/ScreenStocksBridge.csproj` and the `PluginVersion` constant in `plugin/ScreenStocksBridge/Plugin.cs`. Update both values, commit and push the change, then create and push a matching semantic version tag. The workflow rejects a mismatch. For example, version `0.1.0` requires tag `v0.1.0`:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The tag starts the release workflow. It builds against the game and BepInEx assemblies on the self-hosted Windows runner, then publishes the ZIP and its `.sha256` checksum as a GitHub Release. A malformed tag or tag/project version mismatch fails before publication.

## Configure the release runner

Repository maintainers need one Windows x64 self-hosted GitHub Actions runner on a machine with the game installed:

1. Open the repository's **Settings → Actions → Runners → New self-hosted runner**, choose **Windows x64**, and follow GitHub's current download and configuration instructions. Install PowerShell 7 (`pwsh`) on the runner machine, make it available on the runner account's `PATH`, and restart the runner after installation; the Windows build steps explicitly use that shell.
2. Extract the runner into a dedicated directory outside the repository. In the configuration command, add the custom label `screenstocks`; the workflow also requires GitHub's standard `self-hosted`, `Windows`, and `X64` labels.
3. Run the configuration command locally with the temporary registration token shown by GitHub. Do not share the token or save it in the repository. Run the runner as a Windows account that can read the game's BepInEx and Managed assembly folders.
4. Keep the runner software current (the workflow's Node 24 actions require runner version 2.327.1 or later) and keep it online when pushing a release tag. The workflow installs the .NET 8 SDK. If the game is not at the default Steam path used by the project, set the repository Actions variable `SCREENSTOCKS_GAME_DIR` to its installation directory.
5. Restrict repository write access and protect `v*` release tags so only trusted maintainers can create them. The workflow runs on version tags only and does not run on pull requests.

GitHub cautions that self-hosted runners can be compromised by untrusted code, especially in public repositories. Keep pull-request workflows off this runner and allow only trusted maintainers to create release tags; see [GitHub's secure use guidance](https://docs.github.com/en/actions/reference/security/secure-use).

## Python client

Python 3.10 or newer is required. The package has no third-party runtime dependencies. From the repository root, install it for the current Python environment:

```powershell
python -m pip install -e .\python
```

After launching the demo with the bridge installed, get the token from the BepInEx log or `BepInEx\config\screenstocks.bridge.cfg`. The default listener is `127.0.0.1:48721`. Set the connection details in the environment:

```powershell
$env:SCREENSTOCKS_HOST = "127.0.0.1"
$env:SCREENSTOCKS_PORT = "48721"
$env:SCREENSTOCKS_TOKEN = "paste-the-token-here"
```

Watch the current snapshot and changed-market events:

```powershell
python .\examples\market_watch.py
```

## Python API

See the [bridge usage guide](docs/bridge-usage.md) for stock discovery, cooldowns, trade queue behavior, and auto-action controls.

The plugin also automatically claims available level rewards through the game's normal claim method by default. Disable `QualityOfLife.AutoClaimLevelRewards` in `BepInEx\config\screenstocks.bridge.cfg` to turn that behavior off.

```python
from screenstocks_bridge import BridgeClient

with BridgeClient(host="127.0.0.1", port=48721, token="...") as bridge:
    current = bridge.snapshot()
    print(current["cash"], current["stocks"])
    bridge.subscribe_market(lambda event: print(event["event"], event["data"]))
```

`BridgeClient.request(method, params=None)` returns the result dictionary. `snapshot()` returns the in-memory player and visible market snapshot, including manual-trade cooldown durations and remaining time plus a dynamic auto-action slot/action snapshot. `human_activity(stock_id, limit=32, before_tick=None)` fetches one page of aggregate graph activity for a visible stock. `subscribe_market(callback)` delivers event dictionaries on a callback worker thread. Optional typed views are available through `Snapshot.from_dict(data)`, `Stock.from_dict(data)`, `HumanActivity.from_dict(data)`, `HumanActivityPage.from_dict(data)`, `Position.from_dict(data)`, and `TradeReceipt.from_dict(data)`; every view retains its original dictionary in `.raw` for new fields.

## Trades

The API accepts only these actions: `buy_max`, `buy_percent`, `short_max`, `short_percent`, `close_max`, `close_percent`, `sell_max`, `sell_percent`, `cover_max`, and `cover_percent`. Percentage values must be finite and greater than 0 up to 100. A successful RPC response means the trade was submitted to the game's normal public `StockManager` path; `trade.completed` reports whether the game completed or rejected it. Rejection reasons that the game exposes may accompany the event.

Trades change the shared online market. The example requires an explicit action and stock ID, and requires a percentage for percentage actions:

```powershell
python .\examples\submit_trade.py --action buy_percent --stock-id STOCK_ID --percent 10
```

Do not run the example unless you intend to submit that trade. The API validates market readiness, stock visibility and unlock state, trade queue/hold state, available short volume, and position availability; the game's trade methods remain responsible for their own cooldown, affordability, and final server checks.

## Auto actions and cooldowns

`bridge.auto_actions()` lists configured actions with their `slotIndex`, current slot limit, whether another action can be added, global active state, each action's enabled state, and its cooldown duration, end timestamp, and remaining time. Slot capacity and availability come from the game's manager at request time; clients should use returned `slotIndex` values and `canAddAction` instead of assuming a fixed number of slots. The state snapshot also exposes manual buy and short cooldown durations and live remaining time, even when those cooldowns are idle. Times use the game's server clock; `serverClockSynchronized` indicates whether it is synchronized.

The Python client provides `add_auto_action`, `update_auto_action`, `remove_auto_action`, `set_auto_action_enabled`, and `set_auto_actions_active`. Action types are `Buy`, `Short`, `CloseBuy`, and `CloseShort`; conditions are `Above` and `Below`; percentages range from 1 through 100. For example:

```python
state = bridge.auto_actions()
if state["canAddAction"]:
    added = bridge.add_auto_action("STOCK_ID", "Buy", "Below", 12.5, 10)
    slot = added["slotIndex"]
    bridge.set_auto_action_enabled(slot, True)
```

The example configures an action but does not activate global auto actions. Enabling an action or activating the global switch can cause the game to submit trades automatically later according to its normal rules. These controls use public game APIs and normal game persistence; the bridge itself still stores no request, state or trade files.

## Protocol and limits

- TCP bound to loopback (`127.0.0.1`) only; each request includes the configured token.
- Newline-delimited UTF-8 JSON, with a maximum 16 KiB frame.
- Maximum four concurrent clients and 128 queued game-thread requests.
- Unity/game state is read and trade methods are called only from the Unity main thread. Network reads/writes run on background threads.
- Requests use `{ "id", "method", "token", "params" }`; responses use `{ "id", "ok", "result", "error" }`; events use `{ "event", "data" }`.
- Current methods: `state.snapshot`, `state.subscribe`, `market.human_activity`, `trade.submit`, `auto_actions.snapshot`, `auto_actions.add`, `auto_actions.update`, `auto_actions.remove`, `auto_actions.set_enabled`, and `auto_actions.set_active`. Market changes are pushed as `market.updated`; trade completion is pushed as `trade.completed`.

Protocol changes should keep existing fields stable and add new optional fields where possible. Clients should ignore unrecognized fields and may use `.raw` to retain forward-compatible data.

## Troubleshooting

- **Connection refused:** confirm the game is running, the plugin DLL is under `BepInEx\plugins\ScreenStocksBridge\`, and BepInEx logs show `Loopback API listening`.
- **Unauthorized:** copy the current token from the bridge's BepInEx log or config and check `SCREENSTOCKS_TOKEN`. A bad token currently closes the connection without a JSON error body, so the Python client may report `disconnected`.
- **`not_ready`:** the remote market has not completed initialization or has temporarily disconnected; retry `state.snapshot` later.
- **Build cannot find references:** confirm `GameDir` points to the installed demo and that BepInEx 5 is present there.
- **No `market.updated` events:** subscribe after connecting; events are sampled at most four times per second and sent only when the serialized snapshot changes.
- **Slow event callback:** the Python client keeps market updates and trade-completion events in separate bounded queues. If completion processing falls behind, TCP backpressure applies instead of silently discarding a trade result.
- **Fractional buy or short percent:** the game accepts whole percentages for opening trades, so these values are rounded to the nearest whole percentage. Sell, cover, and close percentages retain fractional position sizing.

## License

This project is licensed under MIT No Attribution (`MIT-0`). You may use, copy, modify, publish, distribute, sublicense, and sell copies without attribution or a requirement to preserve the license notice. See [LICENSE](LICENSE) for the full terms, including the warranty and liability disclaimer.

The license covers the original code, documentation, and release files in this project. It does not grant rights to Screen Stocks, Unity, BepInEx, or other third-party materials that are not included here.
