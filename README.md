# Screen Stocks Bridge

Screen Stocks Bridge is a BepInEx plugin and Python client for reading live game state and using the game's normal trade and upgrade actions from Python. It also provides market updates, aggregate graph activity, and auto-action controls.

The plugin listens on `127.0.0.1` only. It does not enable or use the game's official `modSupport` file API, and it does not write bridge snapshots, trade requests, results, or market exports to disk. BepInEx still writes its normal config and log files, and the game can save its own settings. In online mode, the game remains responsible for processing and validating actions submitted through its normal methods. Trades and auto actions can change the shared online market.

## Install the plugin

1. Install Screen Stocks Demo and a compatible BepInEx 5 release.
2. Download the latest `ScreenStocksBridge-v<version>.zip` from [GitHub Releases](https://github.com/fuubox/screen-stocks-bridge/releases).
3. Close the game and extract the ZIP into the Screen Stocks installation folder. The archive contains the plugin directory structure:

   ```text
   BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.dll
   ```

4. Keep the game's `modSupport` setting off, then restart the game.

The release ZIP contains the plugin and project license; it does not install or bundle BepInEx.

## Install the Python client

Python 3.10 or later is required. Install the published client:

```powershell
python -m pip install screenstocks-bridge
```

The PyPI package contains only the Python client. Install the BepInEx plugin separately and leave the game running while you connect.

## Connect to the game

The default listener is `127.0.0.1:48721`. On first launch, the plugin generates an access token. Find it in the BepInEx log or in `BepInEx\config\screenstocks.bridge.cfg`.

To change the listener port, close the game and edit `Port` under `[Bridge]` in `BepInEx\config\screenstocks.bridge.cfg`, then restart the game. The plugin binds only to `127.0.0.1`. Use the same port in your Python client.

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
    print(state["cash"], state["stocks"])
```

## API guide

See the [bridge usage guide](docs/bridge-usage.md) for market and stock discovery, the captured welcome-back summary, graph activity, cooldowns, trades, upgrades, auto actions, and complete examples. The plugin automatically claims available level rewards and captures then closes the welcome-back screen by default. To change either behavior, set `AutoClaimLevelRewards` or `AutoCloseOfflineSummary` under `[QualityOfLife]` in `BepInEx\config\screenstocks.bridge.cfg`, then restart the game.

## Troubleshooting

- **Connection refused:** confirm the game is running, the plugin is installed under `BepInEx\plugins\ScreenStocksBridge\`, and the BepInEx log says `Loopback API listening`. If you changed the port, check that the client uses the same value.
- **Unauthorized or disconnected:** copy the current token from the BepInEx log or config and update `SCREENSTOCKS_TOKEN`.
- **Market not ready:** the bridge requires the online market to finish initializing. Retry when it is ready.

## License

This project is licensed under MIT No Attribution (`MIT-0`). You may use, copy, modify, publish, distribute, sublicense, and sell copies without attribution. See [LICENSE](LICENSE) for the full terms, including the warranty and liability disclaimer. The license does not grant rights to Screen Stocks, Unity, BepInEx, or other third-party materials.
