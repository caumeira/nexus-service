# nexus-service

The local Nexus hardware service. One Native-AOT binary that runs on the user's PC (Windows, macOS, or Linux), talks to the hardware, and exposes a REST + WebSocket API that the [`nexus-web`](https://github.com/hello-nexus/nexus-web) dashboard, the on-device panels, and [`nexus-overlay`](https://github.com/hello-nexus/nexus-overlay) all consume.

This is the engine of [Nexus](https://hellonexus.com). The other repos are clients of this one.

## What it does

- **Sensors / monitoring** - CPU, GPU, RAM, network, disk, fan, temp, FPS, battery (laptop), media sessions. LibreHardwareMonitor on Windows, IOKit on macOS, sysfs/hwmon on Linux.
- **Cooling** - fan curves, pump speed, AIO control. Per-device drivers under `Cooling/` + `QSeries/`.
- **Lighting** - RGB control via a bundled [headless OpenRGB child process](https://github.com/hello-nexus/openrgb-headless), plus first-party HYTE peripheral protocols. Effects engine, screen sync, audio sync, anime mode, game sync (drive your own hardware from a game's lighting: Razer Chroma, Alienware LightFX, and Logitech capture via bundled shims, plus CS2 Game State Integration).
- **Peripherals** - DPI / polling / battery / sleep for supported mice and keyboards (`Peripherals/`).
- **Panel runtimes** - pair + serve the React panel UIs for the HYTE Y70/Y80 secondary touch panel, mobile companion (`/panel/phone`), and Q-Series on-device screens.
- **Apps / widgets** - host for the `nexus.app/1` SDK apps shipped in [`nexus-apps`](https://github.com/hello-nexus/nexus-apps), with sensor bindings and a sandboxed Web Worker runtime. Legacy `nexus.widget/2` manifests still load.
- **Activity** - screen-time, app detection, Steam / Discord / OBS integrations, shortcuts.
- **Remote access** - relay client so the phone panel keeps working away from the LAN (nearest regional relay picked via the cloud API).
- **Webcam** - phone-as-webcam: the mobile companion streams its camera into an OS virtual camera device (`Webcam/`, per-OS backends).
- **Pairing + auth** - local TLS on `:9443` with SPKI-pinned client sessions (the mobile apps and the dashboard), 6-digit pair codes with SAS verification, host-side approval.
- **Tray + lifecycle** - Windows service install / scheduled-task launcher / system tray. macOS launchd. Linux root systemd daemon that adopts the login session for tray and media. Single-instance, self-elevation when needed.

## Ports

- `9400` HTTP (loopback) - default dashboard + panel transport.
- `9443` HTTPS - pairing and remote panel surfaces, served over a locally generated cert. The SPKI of that cert is what gets pinned by clients.
- `6742` TCP (loopback, internal) - OpenRGB SDK server (the headless OpenRGB child process).

See `docs/network-transport.md` for the full polling/topic inventory and `docs/api-spec.md` for the REST surface.

## Develop

Prerequisites: .NET 10 SDK. Node.js 22 LTS or newer if you want the
embedded dashboard (the build pulls it in from a sibling `nexus-web`
checkout).

```sh
# one-time: dashboard dependencies (skip if you don't need the web UI)
cd ../nexus-web && npm install && cd ../nexus-service

dotnet run          # JIT dev build at http://localhost:9400
dotnet test         # xUnit suite
```

Notes:

- The build auto-runs `npm run build:service` in `../nexus-web` and copies
  its `dist/` into `wwwroot/`. Skip that step with
  `dotnet build -p:BuildWeb=false` when iterating on service code only.
- The OpenRGB bundle is optional for development: without binaries under
  `Bundled/<rid>/openrgb/` the service runs with RGB features unavailable.
- Bind address is the first CLI arg: `dotnet run -- http://localhost:9400`.

## Source layout

```
src/
  Program.cs          # AOT minimal-API host bootstrap
  Routes/             # REST endpoint handlers (system, cooling, lighting, devices, ...)
  Sockets/            # multiplex WebSocket + topic auth
  Sensors/            # LibreHardwareMonitor (Win), IOKit (Mac), sysfs (Linux)
  Cooling/  QSeries/  # fan/pump drivers
  Lighting/           # OpenRGB bridge, HYTE protocols, effects, screen+audio sync
  Peripherals/        # mouse/keyboard drivers
  Panel/              # /panel/* pairing + token endpoints
  Widgets/            # nexus.app/1 app host (manifest loader, data sources, worker sandbox)
  Activity/           # screentime, app detection
  Discord/ Steam/ Obs/# third-party integrations
  Media/              # media session state (GSMTC on Windows)
  Fps/                # FPS capture
  Relay/              # off-LAN relay client for the phone panel
  Discovery/          # mDNS/Bonjour advertising
  Auth/  Security/    # local pairing, SPKI pinning, token issuance
  Persistence/        # IConfigStore (per-OS app-data location)
  Net/                # local cert provisioning, loopback discovery
  Lifecycle/          # service install/uninstall, scheduled task, tray entry, CLI flags
  Platform/           # OS-specific shims behind interfaces
  Update/             # OTA self-update engine (IUpdateSource, GitHubReleaseProvider, UpdateService poller, UpdateDownloader, UpdateIntegrity, UpdateInstaller)
  ...                 # supporting subsystems (Devices, Monitoring, Models, Plugins, Telemetry, ...)
docs/
  api-spec.md         # REST surface
  network-transport.md# REST + WebSocket inventory + cadence
  ws-topic-rbac.md    # who may subscribe to which WS topics
Bundled/
  win-x64/            # openrgb + pawnio (at publish root); tools/: adb, ffmpeg, dfu-util, dfu-driver, gamesync, clpeak, primesieve, vkpeak, diskspd, stream
  osx-arm64/          # openrgb (at publish root); tools/: adb, ffmpeg
  linux-x64/          # openrgb (at publish root); tools/: adb, ffmpeg
  macos/  linux/      # tray/status icons, helpers, app icons
installer/
  Nexus.iss           # Inno Setup script
  build-installer.ps1 # Windows installer assembly
  linux/              # tarball packager + install.sh (root systemd daemon)
tests/
  Nexus.Service.Tests       # xUnit, AOT-safe
  Nexus.Service.Benchmarks  # BenchmarkDotNet hot-path CPU/alloc, on-demand
  aot-smoke.sh              # publishes the real AOT binary, smoke-tests JSON endpoints
```

## Build

The project ships as a single AOT binary per OS. Windows targets `net10.0-windows10.0.19041.0` (C#/WinRT projections for GSMTC); macOS and Linux target plain `net10.0`.

```sh
# Windows (from Mac or Windows; cross-compile from Mac is supported)
dotnet publish -c Release -r win-x64 -o publish-win

# macOS (Apple Silicon) - AOT is mandatory, do not pass -p:PublishAot=false
dotnet publish -c Release -r osx-arm64 -o publish-mac

# Linux (glibc x86-64)
dotnet publish -c Release -r linux-x64 -o publish-linux
```

All publishes fail loudly if the bundled OpenRGB binaries aren't present under `Bundled/{rid}/openrgb/`. Build them from [`nexus-rgb`](https://github.com/hello-nexus/nexus-rgb) first - error messages from the csproj spell out the exact commands.

The bundled ffmpeg has no separate repo: it is stock upstream ffmpeg compiled by `scripts/build-ffmpeg-minimal.sh` with a minimal LGPL-only configuration (image/gif/video decode for media import, scale/pad, screen and audio capture). It is optional at build time; produce it once per RID with:

```sh
bash scripts/fetch-ffmpeg.sh all    # or: mac | win | linux
```

## Installers

- **Windows** - after the publish, assemble `Nexus-Setup.exe`:

  ```ps1
  powershell -File installer/build-installer.ps1
  ```

- **Linux** - package the publish output into a tarball; end users extract
  it and run `./install.sh`, which installs a root systemd daemon to
  `/opt/nexus` (no udev rules or group membership needed). See
  `installer/linux/README.md`:

  ```sh
  installer/linux/package.sh publish-linux
  ```

- **macOS** - `Bundled/macos/build-app.sh` wraps the publish output into
  `Nexus.app`, relocating data out of `Contents/MacOS` into `Contents/Resources`
  (symlinked back) so the bundle can be sealed. `Bundled/macos/sign-notarize.sh`
  then deep-signs it with a Developer ID identity, packages `Nexus.dmg`,
  notarizes via `notarytool`, and staples. Set `NEXUS_SKIP_CAMERA_EXTENSION=1`
  to omit the camera system extension (CI, which can't provision it headlessly).

## Test

```sh
dotnet test          # xUnit suite (JIT)
tests/aot-smoke.sh   # publishes the real AOT binary and probes data endpoints
```

All tests are AOT-safe (no reflection-heavy frameworks). Network/parsing/state-machine logic is unit-tested; hardware drivers have provider-interface seams for fake implementations. The AOT smoke script exists because source-generated JSON gaps only show up in the trimmed binary, not under the JIT test host.

The xUnit suite includes allocation-budget guards (`AllocationBudgetTests`) that
assert the per-frame hot paths (lighting canvas, curve evaluation) stay
zero-alloc and cap the monitoring-broadcast serialization. They use the
thread-local GC counter, so they stay fast and deterministic in the default run.

### Benchmarks (on-demand)

`tests/Nexus.Service.Benchmarks` is a BenchmarkDotNet project measuring ns/op and
bytes/op for the service hot paths (JSON broadcast serialization, lighting
canvas ops, curve evaluation). It is not part of any solution or the publish, so
it never affects the shipped binary. Run it explicitly:

```sh
dotnet run -c Release -p:BuildWeb=false \
  --project tests/Nexus.Service.Benchmarks -- --filter '*'
```

## Releases

Installer artifacts are published to [`hello-nexus/nexus-releases`](https://github.com/hello-nexus/nexus-releases) under semver tags (`v3.0.0`, `v3.1.0`, ...): `Nexus-Setup.exe` (Windows) and `Nexus.dmg` (macOS), alongside the mobile app builds from the wrapper repos. The download links on hellonexus.com point at `/releases/latest/download/<asset>`. Each release also carries a `SHA256SUMS` text asset containing the hex-encoded SHA-256 hash of `Nexus-Setup.exe`; the OTA engine uses this for integrity verification before installing.

The canonical version lives in the `VERSION` file at the repo root (e.g. `3.0.0`). The build stamps `"v" + <VERSION content>` into `BuildInfo.Version` at compile time via the `SetGitVersion` MSBuild target.

## Third-party

OpenRGB (GPLv2) ships as a child process, source published at [`hello-nexus/openrgb-headless`](https://github.com/hello-nexus/openrgb-headless). A minimal LGPL-only ffmpeg build (`scripts/build-ffmpeg-minimal.sh`) ships alongside it. All bundled third-party components, including LibreHardwareMonitor, PawnIO, and dfu-util, are detailed with license info in [`THIRD-PARTY.md`](THIRD-PARTY.md).

## License

`nexus-service` is licensed under the **GNU Affero General Public License v3.0**
(AGPL-3.0); see [`LICENSE`](LICENSE) for the full text. Bundled third-party
components retain their own licenses (OpenRGB ships as a separate child process
under GPLv2; see the Third-party section above and [`THIRD-PARTY.md`](THIRD-PARTY.md)).

Copyright (C) 2026 Hello Nexus
