# Screen Stocks Python Bridge

The `screenstocks-bridge` package is a Python client for the local API exposed
by the Screen Stocks Bridge BepInEx plugin. It requires the plugin to be
installed in the game and the game to be running. The package itself does not
install or contact the game backend; it connects to the plugin on loopback.

## Install

```powershell
python -m pip install screenstocks-bridge
```

Install the matching BepInEx plugin from the
[GitHub releases](https://github.com/fuubox/screen-stocks-bridge/releases),
then launch Screen Stocks. The default bridge address is `127.0.0.1:48721`. The plugin port can be changed under `[Bridge]` as `Port` in `BepInEx/config/screenstocks.bridge.cfg`; close and relaunch the game after editing it. Pass the same value to `BridgeClient(port=...)` when it differs from the default. The example scripts also read `SCREENSTOCKS_PORT`.
Read the token from `BepInEx/config/screenstocks.bridge.cfg` or the BepInEx
log.

## Connect

```python
from screenstocks_bridge import BridgeClient

with BridgeClient(token="YOUR_TOKEN") as bridge:
    state = bridge.snapshot()
    print(state["ready"], state["cash"])
    print(bridge.upgrades())
```

The client supports state and activity reads, trades, upgrade purchases, and
auto-action controls. See the
[bridge usage guide](https://github.com/fuubox/screen-stocks-bridge/blob/main/docs/bridge-usage.md)
for the full API and its online behavior.
