# Quick smoke test for Windows - runs service, hits key endpoints, reports results.
# Usage: powershell -File smoke-test-windows.ps1

$ErrorActionPreference = "SilentlyContinue"
$port = 19494
$base = "http://localhost:$port"
$dll = "bin\Debug\net10.0\Nexus.dll"

# Start service as a background job (works over SSH unlike Start-Process).
# Use the dotnet CLI from PATH; fall back to the per-user .NET install.
$dotnetCli = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetCli) { $dotnetCli = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe" }

$job = Start-Job -ScriptBlock {
    param($dotnetCli, $dll, $base)
    & $dotnetCli $dll $base 2>&1
} -ArgumentList $dotnetCli, $dll, $base
Start-Sleep -Seconds 8

$pass = 0
$fail = 0

function Test-Endpoint($verb, $path, $body, $token) {
    $headers = @{}
    if ($token) { $headers["Authorization"] = "Bearer $token" }
    try {
        $params = @{ Uri = "$base$path"; Method = $verb; UseBasicParsing = $true; Headers = $headers }
        if ($body) { $params["Body"] = $body; $params["ContentType"] = "application/json" }
        $r = Invoke-WebRequest @params
        Write-Host "  PASS $verb $path -> $($r.StatusCode)"
        $script:pass++
    } catch {
        $code = $_.Exception.Response.StatusCode.value__
        Write-Host "  FAIL $verb $path -> $code"
        $script:fail++
    }
}

# Ping (public)
Test-Endpoint "GET" "/ping"

# Get token
try {
    $pair = Invoke-RestMethod -Uri "$base/pair" -UseBasicParsing
    $token = $pair.token
    Write-Host "Got token: $($token.Substring(0,8))..."
} catch {
    Write-Host "FATAL: could not get token"
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

# Authenticated endpoints
Test-Endpoint "GET" "/system/cpu/sensors" $null $token
Test-Endpoint "GET" "/system/cpu/model" $null $token
Test-Endpoint "GET" "/system/memory/sensors" $null $token
Test-Endpoint "GET" "/system/memory/total" $null $token
Test-Endpoint "GET" "/system/gpu/sensors" $null $token
Test-Endpoint "GET" "/system/gpu/model" $null $token
Test-Endpoint "GET" "/system/storage/sensors" $null $token
Test-Endpoint "GET" "/system/os-version" $null $token
Test-Endpoint "GET" "/system/volume" $null $token
Test-Endpoint "GET" "/lighting/current" $null $token
Test-Endpoint "GET" "/keeb/settings" $null $token
Test-Endpoint "GET" "/y70/status" $null $token
Test-Endpoint "GET" "/displays" $null $token
Test-Endpoint "GET" "/ping" $null $token
Test-Endpoint "GET" "/pawnio" $null $token
Test-Endpoint "GET" "/start" $null $token

Write-Host ""
Write-Host "Results: $pass passed, $fail failed"

# Cleanup
Stop-Job $job -ErrorAction SilentlyContinue
Remove-Job $job -Force -ErrorAction SilentlyContinue
Get-Process -Name "dotnet" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*nexus*" } | Stop-Process -Force -ErrorAction SilentlyContinue
exit $fail
