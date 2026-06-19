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

# Verify Game Sync shim DLLs are present. They are produced by the nexus-gamesync
# component (build.bat for x64, build32.bat for x86) and must exist before packaging.
$shimX64 = @("RzChromaSDK64.dll", "RzChromatic64.dll", "LightFX.dll", "LogitechLedEnginesWrapper.dll", "LogitechLed.dll")
$shimX86 = @("RzChromaSDK.dll", "RzChromatic.dll", "LightFX.dll", "LogitechLedEnginesWrapper.dll", "LogitechLed.dll")
$shimX64Dir = Join-Path $PublishDir "gamesync\x64"
$shimX86Dir = Join-Path $PublishDir "gamesync\x86"
$missingShims = @()
foreach ($dll in $shimX64) {
    if (-not (Test-Path (Join-Path $shimX64Dir $dll))) { $missingShims += "gamesync\x64\$dll" }
}
foreach ($dll in $shimX86) {
    if (-not (Test-Path (Join-Path $shimX86Dir $dll))) { $missingShims += "gamesync\x86\$dll" }
}
if ($missingShims.Count -gt 0) {
    throw "Game Sync shim DLLs missing from publish dir ($PublishDir):`n  $($missingShims -join "`n  ")`nRun nexus-gamesync build.bat (x64) and build32.bat (x86) first, then re-publish."
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
$dropDir = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
$drop    = Join-Path $dropDir "Nexus-Setup.exe"
Copy-Item $out $drop -Force

$sha256 = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
$sumsLine = "$sha256  Nexus-Setup.exe"
Set-Content -Path (Join-Path $scriptDir "output\SHA256SUMS") -Value $sumsLine -NoNewline
Set-Content -Path (Join-Path $dropDir "SHA256SUMS") -Value $sumsLine -NoNewline

$size = [math]::Round((Get-Item $out).Length / 1MB, 2)
Write-Host ""
Write-Host "Built: $out  ($size MB)"
Write-Host "Drop:  $drop"
Write-Host "SHA256: $sha256"
if ($OpenOutput) { explorer.exe "/select,$drop" }
