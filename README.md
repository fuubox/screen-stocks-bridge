# Screen Stocks Bridge

Screen Stocks Bridge is a BepInEx plugin and Python client for reading live game state and using the game's normal trade and upgrade actions from Python. It also provides market, ticker-news, and auto-action toast updates, aggregate graph activity, and auto-action controls.

The plugin listens on `127.0.0.1` only. It does not enable or use the game's official `modSupport` file API, and it does not write bridge snapshots, trade requests, results, or market exports to disk. BepInEx still writes its normal config and log files, and the game can save its own settings. In online mode, the game remains responsible for processing and validating actions submitted through its normal methods. Trades and auto actions can change the shared online market.

The bridge has offline-reviewed compatibility with Screen Stocks Steam build 26302063. The game now releases its manual-trade queue tail from cleanup `finally` blocks, so the bridge no longer patches that queue. Separate bounded reconnect and recovery hooks remain because the game's ambiguous-trade wait is still unresolved until the original request is reconciled. Live Unity networking, reconnect behavior, and market acceptance remain unverified; offline checks do not replace live-game acceptance checks.

## Install the plugin

1. Install Screen Stocks and a compatible BepInEx 5 release.
2. Download the latest `ScreenStocksBridge-v<version>.zip` from [GitHub Releases](https://github.com/fuubox/screen-stocks-bridge/releases).
3. Close the game and extract the ZIP into the Screen Stocks installation folder. The archive contains the plugin directory structure:

   ```text
   BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.dll
   BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.Core.dll
   ```

4. Keep the game's `modSupport` setting off, then restart the game.

The release ZIP contains the plugin, its core library, and project license; it does not install or bundle BepInEx.

## Install the Python client

Python 3.10 or later is required. Install the published client:

Before using pip, check the [Python package installation security note](docs/python-install-security.md)
for the October 2026 certificate incident. In particular, `pip` must be at
least 26.2.2; upgrading an older pip safely requires a hash-pinned bootstrap.

```powershell
python -m pip install screenstocks-bridge
```

The PyPI package contains only the Python client. Install the BepInEx plugin separately and leave the game running while you connect.

## Connect to the game

The default listener is `127.0.0.1:48721`. On first launch, the plugin generates an access token. Find it in the BepInEx log or in `BepInEx\config\screenstocks.bridge.cfg`.

To change the listener port or the maximum number of simultaneous API client connections, close the game and edit `Port` or `MaxClientConnections` under `[Bridge]` in `BepInEx\config\screenstocks.bridge.cfg`, then restart the game. `MaxClientConnections` defaults to `4` and accepts values from `1` to `256`. Each connected client adds two background threads, so raising the limit can increase load and make the game lag. The plugin binds only to `127.0.0.1`. Use the same port in your Python client.

```powershell
$env:SCREENSTOCKS_HOST = "127.0.0.1"
$env:SCREENSTOCKS_PORT = "48721"
$env:SCREENSTOCKS_TOKEN = "paste-the-token-here"
```

```python
import os
from screenstocks_bridge import BridgeClient

with BridgeClient(
    host=os.getenv("SCREENSTOCKS_HOST", "127.0.0.1"),
    port=int(os.getenv("SCREENSTOCKS_PORT", "48721")),
    token=os.environ["SCREENSTOCKS_TOKEN"],
) as bridge:
    state = bridge.snapshot()
    print(state["cash"], state["netWorth"], state["stocks"])
```

## Optional graph settings

Both graph display patches are disabled by default. To enable one or both, edit
`BepInEx\config\screenstocks.bridge.cfg` and set the desired options under
`[Graph]`:

```ini
[Graph]
CorrectAutoActionMarkers = true
UnstackPurchaseMarkersWithin15Ticks = true
```

- `CorrectAutoActionMarkers` repositions a remote auto-action dot when the
  server confirms it by mapping the authoritative execution timestamp onto the
  graph's server-sample ticks. It chooses the sample nearest in event time; the
  slot's trigger price and Above/Below condition break ties between equally
  close samples. This does not require the graph to show the trigger-price crossing.
  The search ends at the latest server update received at confirmation; if the
  timestamp cannot be mapped to visible history, the game keeps its marker.
  Mapping uses the tick and timestamp from one accepted price update and the
  configured game tick duration, so confirmation delay does not move the dot.
  The price is the nearest graph sample, rather than an exact execution fill.
- `UnstackPurchaseMarkersWithin15Ticks` separates overlapping graph dots within
  15 displayed ticks using vertical lanes with full circle clearance. Buys and
  short covers prefer below their execution price; sells and shorts prefer above.
  Opposite-side overlaps are separated too. Horizontal positions never change.
  Dots remain within the plot; at an edge they may use the other side, and an
  overcrowded plot can still overlap when there is insufficient vertical space.
  It affects graph display only; stored marker ticks and prices are unchanged.
- The activity-focus labels are controlled independently. `ShowTrackingDebugText`
  is an opt-in (default `false`) for the `TRACKING` debug label.
  `ShowOverrideText` is enabled by default; set it to `false` to hide the
  `OVERRIDE: ON` label when activity focus differs from the displayed stock.

When at least one non-API game patch is active, a `BRIDGE-MOD` watermark appears
above the status label at the same size as `TRACKING`. It is independent of the
two label settings; API use alone does not show it.

For example, add these lines under `[Graph]` to show `TRACKING` and hide the
override label:

```ini
ShowTrackingDebugText = true
ShowOverrideText = false
```

Restart the game after editing the config file. These are Screen Stocks Bridge
patches to the graph display. If marker behavior is wrong while either option
is enabled, turn it off and report the issue to the [Screen Stocks Bridge issue
tracker](https://github.com/fuubox/screen-stocks-bridge/issues). Please don't
report bugs caused by these opt-in settings to the original Screen Stocks
developer.

## API guide

The [upgrade quoter guide](docs/upgrade-quoter.md) covers read-only previews for
additional levels, budgets, cash fractions, and target levels or values, including
total cost, resulting value, and calculation limits.

See the [bridge usage guide](docs/bridge-usage.md) for market and stock discovery, current net worth and the saved net-worth chart, ticker-news and auto-action toast subscriptions, the captured welcome-back summary, graph activity, cooldowns, trades, upgrades, auto actions, and complete examples. The plugin automatically claims available level rewards and captures then closes the welcome-back screen by default. To change either behavior, set `AutoClaimLevelRewards` or `AutoCloseOfflineSummary` under `[QualityOfLife]` in `BepInEx\config\screenstocks.bridge.cfg`, then restart the game.

## Troubleshooting

- **Connection refused:** confirm the game is running, the plugin is installed under `BepInEx\plugins\ScreenStocksBridge\`, and the BepInEx log says `Loopback API listening`. If you changed the port, check that the client uses the same value.
- **Unauthorized or disconnected:** copy the current token from the BepInEx log or config and update `SCREENSTOCKS_TOKEN`.
- **Market not ready:** the bridge requires the online market to finish initializing. Retry when it is ready.

## License

This project is licensed under MIT No Attribution (`MIT-0`). You may use, copy, modify, publish, distribute, sublicense, and sell copies without attribution. See [LICENSE](LICENSE) for the full terms, including the warranty and liability disclaimer. The license does not grant rights to Screen Stocks, Unity, BepInEx, or other third-party materials.
