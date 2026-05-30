# nexus-service

The local Nexus hardware service. One Native-AOT binary that runs on the user's PC (Windows) or Mac, talks to the hardware, and exposes a REST + WebSocket API that the [`nexus-web`](https://github.com/nexusqos/nexus-web) dashboard, the on-device panels, and [`nexus-overlay`](https://github.com/nexusqos/nexus-overlay) all consume.

This is the engine of [Nexus](https://hellonexus.com). The other repos are clients of this one.

## What it does

- **Sensors / monitoring** — CPU, GPU, RAM, network, disk, fan, temp, FPS, battery (laptop), Windows GSMTC media sessions. LibreHardwareMonitor on Windows, IOKit on Mac.
- **Cooling** — fan curves, pump speed, AIO control. Per-device drivers under `Cooling/` + `QSeries/`.
- **Lighting** — RGB control across 183 devices via a bundled [headless OpenRGB child process](https://github.com/nexusqos/openrgb-headless), plus first-party HYTE peripheral protocols. Effects engine, screen sync, audio sync, anime mode.
- **Peripherals** — DPI / polling / battery / sleep for supported mice and keyboards (`Peripherals/`).
- **Panel runtimes** — pair + serve the React panel UIs for the HYTE Y70/Y80 secondary touch panel, mobile companion (`/panel/phone`), and Q-Series on-device screens.
- **Widgets** — host for the `nexus.widget/2` SDK widgets shipped in [`nexus-widgets`](https://github.com/nexusqos/nexus-widgets), with declarative views, sensor bindings, and (when capability-granted) a sandboxed Web Worker.
- **Activity** — screen-time, app detection, Steam/Discord integration, shortcuts.
- **Pairing + auth** — local TLS on `:9443` with SPKI-pinned client sessions (the iOS app and the dashboard), 6-digit pair codes with SAS verification, host-side approval.
- **Tray + lifecycle** — Windows service install / scheduled-task launcher / system tray. macOS launchd. Single-instance, self-elevation when needed.

## Ports

- `9400` HTTP (loopback) — default dashboard + panel transport.
- `9443` HTTPS — pairing and remote panel surfaces, served over a locally generated cert. The SPKI of that cert is what gets pinned by clients.
- `6742` TCP (loopback, internal) — OpenRGB SDK server (the headless OpenRGB child process).

See `docs/network-transport.md` for the full polling/topic inventory and `docs/api-spec.md` for the REST surface.

## Source layout

```
src/
  Program.cs          # AOT minimal-API host bootstrap
  Routes/             # REST endpoint handlers (system, cooling, lighting, devices, …)
  Sockets/            # multiplex WebSocket + topic auth
  Sensors/            # LibreHardwareMonitor + IOKit + GSMTC
  Cooling/  QSeries/  # fan/pump drivers
  Lighting/           # OpenRGB bridge, HYTE protocols, effects, screen+audio sync
  Peripherals/        # mouse/keyboard drivers
  Panel/              # /panel/* pairing + token endpoints (incl. PanelPhonePairingService)
  Widgets/            # nexus.widget/2 host (manifest loader, data sources, worker sandbox)
  Activity/           # screentime, app detection
  Discord/  Steam/    # third-party integrations
  Media/              # GSMTC media session state
  Auth/  Security/    # local pairing, SPKI pinning, token issuance
  Persistence/        # IConfigStore (per-OS app-data location)
  Net/                # local cert provisioning, loopback discovery
  Lifecycle/          # service install/uninstall, scheduled task, tray entry, CLI flags
  Platform/           # OS-specific shims behind interfaces
docs/
  api-spec.md         # REST surface
  network-transport.md# REST + WebSocket inventory + cadence
  ws-topic-rbac.md    # who may subscribe to which WS topics
Bundled/
  win-x64/openrgb/    # OpenRGB-headless.exe + DLLs (from nexus-rgb fork)
  win-x64/pawnio/     # PawnIO SMBus modules (RGB DRAM discovery)
  osx-arm64/openrgb/  # OpenRGB-headless macOS binary
  macos/              # status-icon@?x.png for the macOS tray
installer/
  Nexus.iss             # Inno Setup script
  build-installer.ps1 # Windows installer assembly
tests/
  Nexus.Service.Tests   # xUnit, AOT-safe
```

## Build

The project ships as a single AOT binary per OS. Windows targets `net10.0-windows10.0.19041.0` (C#/WinRT projections for GSMTC); macOS targets plain `net10.0`.

```sh
# Windows (from Mac or Windows; cross-compile from Mac is supported)
dotnet publish -c Release -r win-x64 -o publish-win

# macOS (Apple Silicon) — AOT is mandatory, do not pass -p:PublishAot=false
dotnet publish -c Release -r osx-arm64 -o publish-mac
```

Both publishes fail loudly if the bundled OpenRGB binaries aren't present under `Bundled/{rid}/openrgb/`. Build them from [`nexus-rgb`](https://github.com/nexusqos/nexus-rgb) first — error messages from the csproj spell out the exact commands.

The Windows installer (`Nexus-Setup.exe`) is produced separately, after the publish:

```ps1
powershell -File installer/build-installer.ps1
```

The Mac DMG is produced from `publish-mac/` via the master `nexus` repo's release scripts.

## Test

```sh
dotnet test
```

All tests are AOT-safe (no reflection-heavy frameworks). Network/parsing/state-machine logic is unit-tested; hardware drivers have provider-interface seams for fake implementations.

## Releases

Installer artifacts are published to [`nexusqos/nexus-releases`](https://github.com/nexusqos/nexus-releases) as `Nexus-Setup.exe` and `Nexus.dmg` under monotonic `vNN` tags. The download links on hellonexus.com point at `/releases/latest/download/<asset>`.

## Third-party

OpenRGB (GPLv2) ships as a child process, source published at [`nexusqos/openrgb-headless`](https://github.com/nexusqos/openrgb-headless). LibreHardwareMonitor, PawnIO, and the bundled Windows shims are listed in [`THIRD-PARTY.md`](THIRD-PARTY.md).
