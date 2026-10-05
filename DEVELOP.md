# Developer and maintainer guide

This guide covers building, changing, and releasing Screen Stocks Bridge. Player installation and Python usage are in the [README](README.md); the full Python API reference is in [docs/bridge-usage.md](docs/bridge-usage.md).

## Prerequisites

- Screen Stocks Demo installed locally.
- BepInEx 5 installed in that game directory. The plugin references the game's and BepInEx's assemblies directly and does not redistribute them.
- A .NET SDK. CI installs .NET 8; the plugin targets `netstandard2.1`.
- Python 3.10 or later for Python client work.

The project defaults `GameDir` to:

```text
C:\Program Files (x86)\Steam\steamapps\common\Screen Stocks Demo
```

Override it for another installation when building.

## Build and install the plugin locally

From the repository root in PowerShell:

```powershell
dotnet build .\plugin\ScreenStocksBridge\ScreenStocksBridge.csproj -c Release
```

For a non-default game path:

```powershell
dotnet build .\plugin\ScreenStocksBridge\ScreenStocksBridge.csproj -c Release -p:GameDir="D:\Games\Screen Stocks Demo"
```

The output directory contains `ScreenStocksBridge.dll` and its dependency `ScreenStocksBridge.Core.dll`. To try it in game, close Screen Stocks and copy both files into:

```text
<GameDir>\BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.dll
<GameDir>\BepInEx\plugins\ScreenStocksBridge\ScreenStocksBridge.Core.dll
```

Restart the game to load it. Keep the game's official `modSupport` setting off; the bridge uses its own authenticated loopback TCP API.

## Repository map

| Path | Purpose |
| --- | --- |
| `plugin/ScreenStocksBridge/Plugin.cs` | BepInEx entry point, config binding, and Unity update loop |
| `plugin/ScreenStocksBridge/BridgeServer.cs` | Loopback TCP listener and connection lifecycle |
| `plugin/ScreenStocksBridge/*Service.cs` | In-memory game state reads and allowlisted game actions |
| `core/ScreenStocksBridge.Core/` | Unity-independent protocol models, JSON serialization, parsing, and pure request validation |
| `tests/ScreenStocksBridge.Core.Tests/` | .NET tests for protocol and request validation |
| `tests/python/` | Python client tests against a fake JSON-lines bridge |
| `tools/game-compatibility.json` | Supported local game/BepInEx fingerprint and required API signatures |
| `tools/check-game-compatibility.ps1` | Binary fingerprint and compatibility-surface checker |
| `python/src/screenstocks_bridge/` | Python client, protocol errors, and optional typed views |
| `examples/` | Small scripts that use the installed Python client |
| `docs/bridge-usage.md` | User-facing API, market data, trades, upgrades, actions, and cooldown reference |
| `docs/testing-and-game-compatibility.md` | Automated checks and game-patch review procedure |
| `.github/workflows/ci.yml` | PR-safe Python/core checks and trusted game-runner compatibility build |
| `.github/workflows/release.yml` | Tagged Windows plugin build and GitHub Release publication |
| `.github/workflows/publish-python.yml` | PyPI package build and Trusted Publisher upload |

The plugin accepts socket requests on background threads but queues game work for the Unity main thread. Keep Unity and game-manager access on that thread. Python state snapshots are read from live in-process game objects; the plugin does not implement the game's backend protocol.

## Run automated checks

From the repository root:

```powershell
$env:PYTHONPATH = 'python/src'
python -m unittest discover -s tests/python -v
dotnet test .\tests\ScreenStocksBridge.Core.Tests\ScreenStocksBridge.Core.Tests.csproj -c Release
```

The test projects cover the Python JSON-lines client and Unity-independent C# protocol/validation code. To check the installed game surface and build against its assemblies:

```powershell
.\tools\check-game-compatibility.ps1
dotnet build .\plugin\ScreenStocksBridge\ScreenStocksBridge.csproj -c Release
```

See [the compatibility and testing guide](docs/testing-and-game-compatibility.md) for the patch-review process and in-game smoke checks.

## Develop the Python client

Install the local package in editable mode from the repository root:

```powershell
python -m pip install -e .\python
```

The client has no third-party runtime dependencies. Use the scripts under `examples/` from the repository root. Trade examples submit commands to the running game and can change the shared online market; only run them intentionally.

## Version and changelog

Before preparing a release, keep these version values identical:

- `<Version>` in `core/ScreenStocksBridge.Core/ScreenStocksBridge.Core.csproj`
- `<Version>` in `plugin/ScreenStocksBridge/ScreenStocksBridge.csproj`
- `PluginVersion` in `plugin/ScreenStocksBridge/Plugin.cs`
- `version` in `python/pyproject.toml`

Add a matching `## [X.Y.Z] - YYYY-MM-DD` section to `CHANGELOG.md`. The release workflow checks the plugin's project and source versions against the tag and uses the changelog section as GitHub Release notes.

## Publish a release

1. Update the three version values and add the changelog section.
2. Build and review the changes, then commit and push them to `main`.
3. Create and push the matching semantic version tag. For example:

   ```powershell
   git tag v0.3.0
   git push origin v0.3.0
   ```

The tag starts `.github/workflows/release.yml`. It builds the plugin against the game and BepInEx assemblies, packages the DLL and license into an install-ready ZIP, creates a SHA-256 checksum, and publishes both as a GitHub Release. Publishing the GitHub Release starts `.github/workflows/publish-python.yml`, which builds the wheel and source distribution from the matching tag and publishes them to PyPI.

The Python workflow can also be started manually with a tag input. It validates the tag against `python/pyproject.toml` and requires the matching source revision to be checked out.

## GitHub Actions configuration

### Plugin release runner

The plugin build requires one Windows x64 self-hosted GitHub Actions runner on a machine with the game and BepInEx installed. Configure it with the custom `screenstocks` label in addition to GitHub's standard `self-hosted`, `Windows`, and `X64` labels. The workflow uses the Windows PowerShell shell and installs .NET 8. If the game is not at the default Steam path, set the repository Actions variable `SCREENSTOCKS_GAME_DIR` to its installation directory.

Transaction-history cache and request pacing are configured in `BepInEx/config/screenstocks.bridge.cfg` under `[TransactionHistory]`. `CacheSeconds` (default and hard minimum `60`) controls how long results are considered fresh. `MinimumRequestIntervalSeconds` (default and hard minimum `30`) sets the minimum gap before the bridge starts another request after any observed game transaction-history request. Higher values are allowed; lower values are raised to the minimum and saved back to the config. Restart the game after editing the file. The Transactions screen's own successful responses also seed the in-memory cache and start this bridge cooldown. The plugin does not suppress requests made by the game UI itself.

Keep the runner online for release tags and its software current (the Node 24 actions require runner version 2.327.1 or later). Restrict write access and protect `v*` tags. The workflow is tag-triggered; do not enable untrusted pull-request code on this runner. GitHub's [secure use guidance](https://docs.github.com/en/actions/reference/security/secure-use) explains the risks of self-hosted runners in public repositories.

### PyPI Trusted Publisher

The Python workflow uses GitHub Actions OIDC and does not need a PyPI API token or repository secret. Configure a PyPI Trusted Publisher with:

- Project: `screenstocks-bridge`
- Owner: `fuubox`
- Repository: `screen-stocks-bridge`
- Workflow: `publish-python.yml`
- GitHub Actions environment: `pypi`

The workflow publishes from the `pypi` environment when a GitHub Release is published.

## License

The project is released under MIT No Attribution (`MIT-0`). See [LICENSE](LICENSE). The license applies to original project material and does not grant rights to third-party game, Unity, or BepInEx materials.
