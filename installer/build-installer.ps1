# Builds Nexus-Setup.exe from an existing AOT publish output.
#
# This is NOT part of the regular AOT publish cycle. Run it explicitly when you
# want a fresh installer (e.g. before a release). The dev workflow stays:
#   dotnet publish -c Release -r win-x64 -o <publish-dir>
#   schtasks /Run /TN NexusService
#
# Usage (from the PC, in any shell):
#   powershell -File installer\build-installer.ps1
#
# By default the script looks for the publish output at ..\..\aot relative to
# this script (i.e. <repo>/aot if the layout is <repo>/service/installer).
# Override with -PublishDir for a custom layout.
#
# Optional: -PublishDir <path>  override the AOT publish dir
#           -OpenOutput          reveal the resulting Setup.exe in Explorer

param(
    [string]$PublishDir = "",
    [switch]$OpenOutput
)

if ([string]::IsNullOrEmpty($PublishDir)) {
    $scriptDirEarly = Split-Path -Parent $MyInvocation.MyCommand.Path
    $PublishDir = (Resolve-Path (Join-Path $scriptDirEarly "..\..\aot") -ErrorAction SilentlyContinue).Path
    if ([string]::IsNullOrEmpty($PublishDir)) {
        $PublishDir = Join-Path $scriptDirEarly "..\..\aot"
    }
}

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$iss = Join-Path $scriptDir "Nexus.iss"

# Probe standard Inno Setup 6 install locations: per-user (winget default),
# then both Program Files variants (machine-wide / Chocolatey on CI).
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not (Test-Path $iss))  { throw "Missing Nexus.iss next to this script: $iss" }
if (-not $iscc) { throw "Inno Setup 6 not installed. Run: winget install JRSoftware.InnoSetup (or 'choco install innosetup')" }
if (-not (Test-Path (Join-Path $PublishDir "Nexus.exe"))) {
    throw "AOT publish not found at $PublishDir. Run dotnet publish first."
}

# Strip any leftover macOS AppleDouble files from the publish dir (they slip in
# through scp/tar from a Mac dev machine and break Inno's compressor).
[System.IO.Directory]::EnumerateFiles($PublishDir, "._*", "AllDirectories") |
    ForEach-Object { [System.IO.File]::Delete("\\?\" + $_) }

# Fail if wwwroot/assets holds a bundle unreachable from index.html. The
# BuildWeb=false publish path skips the wwwroot wipe in Nexus.Service.csproj, so
# bundles from earlier builds accumulate; this asserts every bundle belongs to
# the current build. Closure: seed from index.html, expand through inter-chunk
# references (Vite hashed basenames), flag the rest.
$assetsDir = Join-Path $PublishDir "wwwroot\assets"
$indexHtml = Join-Path $PublishDir "wwwroot\index.html"
if ((Test-Path $assetsDir) -and (Test-Path $indexHtml)) {
    $all = @(Get-ChildItem $assetsDir -File |
        Where-Object { $_.Extension -eq '.js' -or $_.Extension -eq '.css' } |
        ForEach-Object { $_.Name })
    $reach = [System.Collections.Generic.HashSet[string]]::new()
    $indexText = Get-Content $indexHtml -Raw
    foreach ($f in $all) { if ($indexText.Contains($f)) { [void]$reach.Add($f) } }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($f in @($reach)) {
            if (-not $f.EndsWith('.js')) { continue }
            $text = Get-Content (Join-Path $assetsDir $f) -Raw
            foreach ($g in $all) {
                if (-not $reach.Contains($g) -and $text.Contains($g)) {
                    [void]$reach.Add($g); $changed = $true
                }
            }
        }
    }
    $orphans = @($all | Where-Object { -not $reach.Contains($_) })
    if ($orphans.Count -gt 0) {
        Write-Host "Orphaned bundles in $assetsDir (stale, not reachable from index.html):"
        $orphans | ForEach-Object { Write-Host "  $_" }
        throw "wwwroot is not clean: $($orphans.Count) orphaned bundle(s). Rebuild nexus-web (which wipes wwwroot) before packaging."
    }
    Write-Host "wwwroot clean: $($all.Count) bundles, 0 orphaned."
}

Push-Location $scriptDir
try {
    & $iscc /DPublishDir="$PublishDir" Nexus.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$out = Join-Path $scriptDir "output\Nexus-Setup.exe"
# Drop a copy at the parent nexus/ dir so the latest installer always lives
# next to the other top-level nexus artifacts, not buried in installer\output.
$dropDir = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
$drop    = Join-Path $dropDir "Nexus-Setup.exe"
Copy-Item $out $drop -Force

$size = [math]::Round((Get-Item $out).Length / 1MB, 2)
Write-Host ""
Write-Host "Built: $out  ($size MB)"
Write-Host "Drop:  $drop"
if ($OpenOutput) { explorer.exe "/select,$drop" }
