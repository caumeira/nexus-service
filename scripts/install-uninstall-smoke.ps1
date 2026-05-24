#requires -Version 5.1
<#
.SYNOPSIS
    End-to-end smoke test for the Nexus install / uninstall primitives.

.DESCRIPTION
    Runs against a freshly-published AOT build at -PublishDir. Performs:

      1. Pre-clean (stop+delete any existing NexusService, remove install dir).
      2. --install via a SYSTEM-context scheduled task (no UAC over SSH).
      3. Verify: service registered, running, DACL grant present, firewall
         rule open, Add/Remove Programs reg key written, /ping responds.
      4. Recovery path: sc.exe stop NexusService, then Nexus.exe --start-service
         from an UNPRIVILEGED process. Service should come back up with NO
         UAC prompt because of the SERVICE_START DACL grant.
      5. --uninstall via SYSTEM-context scheduled task.
      6. Verify: service gone, reg key gone, firewall rule gone, install dir
         deleted (or scheduled for delete-on-reboot).

    Prints a PASS/FAIL summary and exits non-zero on any failure.

.PARAMETER PublishDir
    Path to the AOT-published Nexus.exe directory. Default is "..\..\aot"
    relative to the script's parent folder.

.EXAMPLE
    powershell -File scripts\install-uninstall-smoke.ps1
    powershell -File scripts\install-uninstall-smoke.ps1 -PublishDir C:\Users\me\nexus\aot
#>

param(
    [string]$PublishDir = ""
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrEmpty($PublishDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $PublishDir = (Resolve-Path (Join-Path $scriptDir "..\..\aot") -ErrorAction SilentlyContinue).Path
    if ([string]::IsNullOrEmpty($PublishDir)) {
        $PublishDir = Join-Path $scriptDir "..\..\aot"
    }
}

$Service     = "NexusService"
$InstallDir  = Join-Path ${env:ProgramFiles} "Nexus"
$Binary      = Join-Path $InstallDir "Nexus.exe"
$FirewallRule = "NexusService"
$UninstallKey = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Nexus"

$pass = 0
$fail = 0
function P($msg) { Write-Host "  PASS $msg" -ForegroundColor Green; $script:pass++ }
function F($msg) { Write-Host "  FAIL $msg" -ForegroundColor Red;   $script:fail++ }
function Section($name) { Write-Host ""; Write-Host "==> $name" -ForegroundColor Cyan }

function Invoke-OneShotElevated([string]$Command, [int]$WaitSeconds = 30) {
    # SSH sessions are unelevated, but we are an admin user. Spawn a
    # one-shot scheduled task that runs as SYSTEM (HIGHEST run level) so
    # the command gets a fully elevated token without a UAC prompt.
    $taskName = "NexusSmoke_" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $tr = $Command -replace '"', '\"'
    schtasks /Create /TN $taskName /TR $tr /SC ONCE /ST 00:00 /RL HIGHEST /F /RU SYSTEM | Out-Null
    schtasks /Run /TN $taskName | Out-Null
    Start-Sleep -Seconds $WaitSeconds
    schtasks /Delete /TN $taskName /F | Out-Null
}

# -----------------------------------------------------------------------------
# 0. Pre-clean
# -----------------------------------------------------------------------------
Section "Pre-clean any prior install"
sc.exe stop $Service 2>$null | Out-Null
Start-Sleep 2
sc.exe delete $Service 2>$null | Out-Null
Stop-Process -Name Nexus -Force -ErrorAction SilentlyContinue
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $UninstallKey -Recurse -ErrorAction SilentlyContinue
netsh advfirewall firewall delete rule name=$FirewallRule | Out-Null
P "pre-clean complete"

if (-not (Test-Path (Join-Path $PublishDir "Nexus.exe"))) {
    F "Nexus.exe not found at $PublishDir - run dotnet publish first"
    exit 1
}

# -----------------------------------------------------------------------------
# 1. --install via SYSTEM-context one-shot task
# -----------------------------------------------------------------------------
Section "--install"
$installCmd = "`"$(Join-Path $PublishDir 'Nexus.exe')`" --install"
Invoke-OneShotElevated -Command $installCmd -WaitSeconds 40

# -----------------------------------------------------------------------------
# 2. Post-install assertions
# -----------------------------------------------------------------------------
Section "Post-install state"
$svc = Get-Service $Service -ErrorAction SilentlyContinue
if ($svc) { P "service is registered" } else { F "service NOT registered" }
if ($svc -and $svc.Status -eq "Running") { P "service is Running" } else { F "service not Running (status=$($svc.Status))" }

$cfg = sc.exe qc $Service 2>$null
if ($cfg -match "AUTO_START") { P "start type is Auto" } else { F "start type not Auto" }
if ($cfg -match "LocalSystem") { P "account is LocalSystem" } else { F "account not LocalSystem" }
if ($cfg -match "PawnIO") { P "depends on PawnIO" } else { F "no PawnIO dependency" }

$sd = sc.exe sdshow $Service 2>$null
if ($sd -match "\(A;;LCRP;;;AU\)") { P "DACL grants SERVICE_START to Authenticated Users" }
else { F "DACL does NOT grant SERVICE_START to AU" }

# Add/Remove Programs registration is owned by Inno Setup (the _is1 key),
# not by Nexus.exe --install. Bare-EXE smoke tests therefore should NOT see
# an HKLM\...\Uninstall\Nexus entry; if one is present it's stale from an
# older build and the install path is supposed to clear it.
$reg = Get-ItemProperty -Path $UninstallKey -ErrorAction SilentlyContinue
if (-not $reg) { P "no stale legacy Uninstall\Nexus reg key" }
else { F "unexpected legacy Uninstall\Nexus reg key present after --install" }

$fw = netsh advfirewall firewall show rule name=$FirewallRule 2>$null
if ($fw -match "Enabled.*Yes") { P "firewall rule enabled" }
else { F "firewall rule missing or disabled" }

try {
    $ping = Invoke-RestMethod -Uri "http://localhost:9400/ping" -TimeoutSec 5
    if ($ping.service -eq "nexus-service" -and $ping.initialized) { P "/ping returns initialized=true" }
    else { F "/ping returned unexpected payload" }
} catch {
    F "/ping unreachable: $($_.Exception.Message)"
}

# -----------------------------------------------------------------------------
# 3. Recovery path: stop + unprivileged restart with no UAC
# -----------------------------------------------------------------------------
Section "Recovery path (stop + unprivileged --start-service)"
# Stop the service (requires admin; we ARE admin in this PS session via the
# one-shot model, but a real user would hit services.msc which auto-elevates).
Invoke-OneShotElevated -Command "sc.exe stop $Service" -WaitSeconds 5
Start-Sleep 2
$status = (Get-Service $Service).Status
if ($status -eq "Stopped") { P "service stopped on demand" }
else { F "service did not stop (status=$status)" }

# Run --start-service from THIS unprivileged PowerShell session.
& $Binary --start-service | Out-Null
Start-Sleep 3
$status2 = (Get-Service $Service).Status
if ($status2 -eq "Running") { P "service restarted via --start-service (no UAC)" }
else { F "--start-service did not bring it back (status=$status2)" }

# -----------------------------------------------------------------------------
# 4. --uninstall
# -----------------------------------------------------------------------------
Section "--uninstall"
$uninstallCmd = "`"$Binary`" --uninstall"
Invoke-OneShotElevated -Command $uninstallCmd -WaitSeconds 25

# -----------------------------------------------------------------------------
# 5. Post-uninstall assertions
# -----------------------------------------------------------------------------
Section "Post-uninstall state"
$svc2 = Get-Service $Service -ErrorAction SilentlyContinue
if (-not $svc2) { P "service removed" } else { F "service still present" }

if (-not (Test-Path $UninstallKey)) { P "Add/Remove Programs reg key gone" }
else { F "Add/Remove Programs reg key still present" }

$fw2 = netsh advfirewall firewall show rule name=$FirewallRule 2>&1
if ($fw2 -match "No rules") { P "firewall rule removed" }
else { F "firewall rule still present" }

# Install dir may persist if Nexus.exe held its own lock (scheduled for
# delete-on-reboot). Accept either gone OR present-but-shrunk.
if (-not (Test-Path $InstallDir)) {
    P "install dir removed cleanly"
} else {
    $remaining = (Get-ChildItem $InstallDir -Recurse -ErrorAction SilentlyContinue).Count
    if ($remaining -le 5) {
        P "install dir mostly gone ($remaining items pending delete-on-reboot)"
    } else {
        F "install dir still has $remaining files"
    }
}

# -----------------------------------------------------------------------------
# Summary
# -----------------------------------------------------------------------------
Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
Write-Host "PASS: $pass" -ForegroundColor Green
Write-Host "FAIL: $fail" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
exit $(if ($fail -eq 0) { 0 } else { 1 })
