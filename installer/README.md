# Installer

Builds `Nexus-Setup.exe`, the single signed executable end users download
to install Nexus. Wraps the AOT publish output in an Inno Setup 6 wizard
that lays files into `C:\Program Files\Nexus\`, installs the PawnIO kernel
driver, registers the `NexusService` scheduled task (At Logon, elevated),
starts the service, and opens the dashboard in the browser.

This is **not part of the regular AOT publish cycle**. The dev loop stays:

```
dotnet publish ...
schtasks /Run /TN NexusService
```

The installer is built explicitly when shipping a release.

## One-time setup (PC only)

```powershell
winget install JRSoftware.InnoSetup
```

Inno Setup 6 lands at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.

## Building

The installer wraps an existing AOT publish output. Under the current
deploy flow the service publishes **directly into `C:\Program Files\Nexus\`**
(see the `build-pc` runbook), so point the script there explicitly - the
script's legacy `..\..\aot` default no longer exists:

```powershell
powershell -File installer\build-installer.ps1 -PublishDir "$env:ProgramFiles\Nexus"
```

Output: `installer\output\Nexus-Setup.exe` (~19 MB compressed), with a copy
dropped at `%USERPROFILE%\nexus\Nexus-Setup.exe`. Releases are published as a
monotonic `vNN` tag on `hello-nexus/nexus-releases` via `gh release create`.

Optional flags:
- `-PublishDir <path>`  the AOT publish dir to wrap (pass `$env:ProgramFiles\Nexus`)
- `-OpenOutput`         open Explorer at the resulting file

### Keep it lean

The installer wraps the publish tree **verbatim**, so anything stray in
`C:\Program Files\Nexus\` ships inside it. The installer is lzma2/max
compressed, so an unexpected multi-MB size jump vs the previous release is a
signal worth checking - diff against the last `Nexus-Setup.exe`. A jump can be
intended (a new bundled feature) or junk; verify which.

- **Intended payload:** the firmware flasher binaries (`dfu-util\`,
  `dfu-driver\`, ~4.4 MB) ship in release - end users flash firmware upgrades
  through them (this is what grew v69 = 24 MB vs v68 = 18.7 MB; the firmware
  flasher feature landed between those tags). They are *not* junk. `DevTools`
  only unlocks the brick-risky cross-variant / downgrade paths in
  `FirmwareFlasher.cs`, not flashing itself, so don't gate the binaries on it.
- **Actual junk to strip:** delete any `test-results\` (a `dotnet test` runner
  artifact the Web SDK content glob pulls into publish) and confirm
  `wwwroot\assets\` holds only the current build's hashed bundles (a skipped
  wwwroot wipe accumulates every prior build's dead `*.js`).

## Files

- `Nexus.iss` - Inno Setup script (wizard config, install steps, uninstall)
- `logo-small.bmp` - 58x58 logo shown top-right of the directory page; regenerated
  from `..\icon.ico` if you change the brand mark
- `build-installer.ps1` - the build entry point, also strips macOS AppleDouble
  files from the publish dir (they slip in via scp from Mac dev machines)
- `output\` - compiler output (gitignored)

## Install scope (per-user vs all-users)

Default is per-user (`%LOCALAPPDATA%\Programs\Nexus`). The directory page
includes a single "Install for all users on this PC" checkbox that flips the
target to `C:\Program Files\Nexus` when checked. No extra wizard pages.

UAC is required either way because the PawnIO kernel driver install is
machine-wide (Windows has no per-user kernel drivers). The win of per-user is
that *future binary updates* can rewrite the exe without UAC. The all-users
choice exists for shared-PC scenarios where every Windows account on the
machine needs a single shared install.

The autostart scheduled task (`NexusService`, At Logon, elevated) is
always registered for the launching user via `/RU "{username}"`. Other users
on a per-machine install can still launch the exe manually, but won't get
auto-start unless they re-register the task themselves.

## Code signing (TODO before shipping)

The current build is unsigned, so users see a SmartScreen "Windows protected
your PC" prompt. Three things need an Authenticode signature:

1. `Nexus.exe` (signed before being bundled into the installer)
2. `OpenRGB-headless.exe` (already shipped from the openrgb bundle)
3. `Nexus-Setup.exe` (signed after Inno produces it)

EV cert (~$300-500/yr, USB token) eliminates SmartScreen warnings on day one.
OV cert (~$60-200/yr) needs reputation to build before SmartScreen relents.

`signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a Nexus-Setup.exe`

PawnIO.sys is already WHQL-signed by its author, no action needed.
