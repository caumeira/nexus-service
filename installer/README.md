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

Output: `installer\output\Nexus-Setup.exe` (~19 MB compressed) plus a
`SHA256SUMS` next to it, with copies of both dropped at `%USERPROFILE%\nexus\`.
Releases are published as a semver `vX.Y.Z` tag (matching `VERSION`) on
`hello-nexus/nexus` via `gh release create`, and must carry the
`SHA256SUMS` asset - the OTA updater requires the published hash to auto-stage a
release.

Optional flags:
- `-PublishDir <path>`  the AOT publish dir to wrap (pass `$env:ProgramFiles\Nexus`)
- `-OpenOutput`         open Explorer at the resulting file
- `-Sign`               Authenticode-sign the first-party binaries and the
  installer via Azure Artifact Signing (see Code signing below); omit for a
  fast unsigned dev build
- `-SignToolPath` / `-DlibPath`  override the auto-probed signtool / dlib paths

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
- `signing-metadata.json` - Artifact Signing account/profile/endpoint (non-secret)
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

## Code signing

Pass `-Sign` to Authenticode-sign the release via **Azure Artifact Signing**
(formerly "Trusted Signing"), under the **American Future Technology Corp**
publisher identity. The script signs `Nexus.exe` and `nexus-overlay.exe` before
Inno packages them, then `Nexus-Setup.exe` after Inno produces it but before the
`SHA256SUMS` hash, so the published hash (and the OTA integrity check) covers the
signed bytes. Bundled third-party binaries (`OpenRGB-headless.exe`, `adb.exe`)
are launched by the signed service, never directly by the user, so they carry no
mark-of-the-web and are left unsigned. `PawnIO.sys` is WHQL-signed by its author.

Account/profile/endpoint live in `signing-metadata.json` (non-secret). The signer
authenticates with `DefaultAzureCredential`:

- **CI** (`hello-nexus/nexus` `.github/workflows/ci.yml`): `azure/login` via OIDC
  federated credentials (no stored secret), then `build-installer.ps1 -Sign`.
- **Local / build-pc**: install the client tools once and provide the
  service-principal env vars, then run with `-Sign`:

  ```powershell
  winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
  $env:AZURE_TENANT_ID="..."; $env:AZURE_CLIENT_ID="..."; $env:AZURE_CLIENT_SECRET="..."
  powershell -File installer\build-installer.ps1 -PublishDir "$env:ProgramFiles\Nexus" -Sign
  ```

The client tools bundle a compatible signtool + `Azure.CodeSigning.Dlib.dll`; the
dlib does **not** work with the 10.0.20348 Windows SDK. Certs are valid only 72h,
so timestamping (`http://timestamp.acs.microsoft.com`, baked into the script) is
mandatory: it keeps a signature valid after the cert rotates daily.
