# GTA11Y diagnostic bridge

The diagnostic bridge is a loopback-only, line-delimited JSON service inside the running
GTA V script. It lets a developer inspect the exact state driving the accessibility mod
without restarting the game or inferring live state from a later log.

## Safety and enablement

- It binds only to `127.0.0.1`, defaults to port `27738`, and has no authentication.
- `tools\deploy.ps1` writes `scripts\gta11y-bridge.json` with `enabled: true` for this
  primary test machine.
- `tools\package.ps1` writes the same file with `enabled: false` in every public `dist`
  package. A recipient must deliberately change that value to enable it.
- Socket threads never touch SHVDN entities. Requests are queued and executed from
  `GTA11Y.onTick`; a stopped game tick produces a bounded timeout instead of unsafe reads.
- Input holds expire automatically after at most 30 seconds. `input.release` without a
  control name releases every bridge-owned control.

## Clients

Use the plain client for manual diagnostics:

```powershell
python tools\gta11y-cli.py ping
python tools\gta11y-cli.py state
python tools\gta11y-cli.py scanner kind=vehicles radius=100 max_results=10
python tools\gta11y-cli.py input.hold control=accelerate value=0.25 ms=500
python tools\gta11y-cli.py input.release
python tools\gta11y-cli.py screenshot name=unexpected-divider
python tools\gta11y-cli.py log.write source=codex text="Beginning divider recovery test"
python tools\gta11y-cli.py speech.sapi text="Please hold the throttle steady"
```

`.mcp.json` registers the dependency-free MCP stdio wrapper in
`tools\mcp\gta11y_mcp.py`. Its screenshot tool returns both the local JPEG path and MCP
image content.

## Command groups

- `ping`, `help`, `mod.state`, `state`
- `player`, `vehicle`
- `drive.state`, `shapecasts`
- `scanner` for vehicles, people, and objects
- `input.controls`, `input.hold`, `input.release`, `input.state`
- `screenshot`
- `log.tail`, `log.write`
- `speech.sapi`, `speech.status`

`log.write` emits `EVENT bridge-marker` with the current drive-log frame and an explicit
source. It refuses when drive-assist logging is off, preventing an attribution marker from
silently going nowhere.

SAPI speech runs on a dedicated STA worker and does not enter GTA11Y's Tolk/NVDA speech
queue. This is intentional: it gives an external tester a separate communication path
during live drive-assist experiments.
