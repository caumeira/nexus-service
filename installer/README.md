# Installer

Builds `qOS-Setup.exe`, the single signed executable end users download
to install qOS. Wraps the AOT publish output in an Inno Setup 6 wizard
that lays files into `C:\Program Files\qOS\`, installs the PawnIO kernel
driver, registers the `QosService` scheduled task (At Logon, elevated),
starts the service, and opens the dashboard in the browser.

This is **not part of the regular AOT publish cycle**. The dev loop stays:

```
dotnet publish ...
schtasks /Run /TN QosService
```

The installer is built explicitly when shipping a release.

## One-time setup (PC only)

```powershell
winget install JRSoftware.InnoSetup
```

Inno Setup 6 lands at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.

## Building

After a successful `dotnet publish` into a sibling `aot\` dir (the default
script lookup is `..\..\aot` relative to this folder):

```powershell
powershell -File installer\build-installer.ps1
```

Output: `installer\output\qOS-Setup.exe` (~17 MB compressed).

Optional flags:
- `-PublishDir <path>`  override the AOT publish dir
- `-OpenOutput`         open Explorer at the resulting file

## Files

- `Qos.iss` - Inno Setup script (wizard config, install steps, uninstall)
- `logo-small.bmp` - 58x58 logo shown top-right of the directory page; regenerated
  from `..\icon.ico` if you change the brand mark
- `build-installer.ps1` - the build entry point, also strips macOS AppleDouble
  files from the publish dir (they slip in via scp from Mac dev machines)
- `output\` - compiler output (gitignored)

## Install scope (per-user vs all-users)

Default is per-user (`%LOCALAPPDATA%\Programs\qOS`). The directory page
includes a single "Install for all users on this PC" checkbox that flips the
target to `C:\Program Files\qOS` when checked. No extra wizard pages.

UAC is required either way because the PawnIO kernel driver install is
machine-wide (Windows has no per-user kernel drivers). The win of per-user is
that *future binary updates* can rewrite the exe without UAC. The all-users
choice exists for shared-PC scenarios where every Windows account on the
machine needs a single shared install.

The autostart scheduled task (`QosService`, At Logon, elevated) is
always registered for the launching user via `/RU "{username}"`. Other users
on a per-machine install can still launch the exe manually, but won't get
auto-start unless they re-register the task themselves.

## Code signing (TODO before shipping)

The current build is unsigned, so users see a SmartScreen "Windows protected
your PC" prompt. Three things need an Authenticode signature:

1. `qOS.exe` (signed before being bundled into the installer)
2. `OpenRGB-headless.exe` (already shipped from the openrgb bundle)
3. `qOS-Setup.exe` (signed after Inno produces it)

EV cert (~$300-500/yr, USB token) eliminates SmartScreen warnings on day one.
OV cert (~$60-200/yr) needs reputation to build before SmartScreen relents.

`signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a qOS-Setup.exe`

PawnIO.sys is already WHQL-signed by its author, no action needed.
