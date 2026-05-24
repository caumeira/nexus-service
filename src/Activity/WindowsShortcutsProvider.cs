using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public sealed class WindowsShortcutsProvider : IShortcutsProvider
{
    private static readonly TimeSpan AppListCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IconCacheTtl = TimeSpan.FromHours(1);

    private List<Shortcut>? _appCache;
    private DateTime _appCacheExpiry;
    private readonly object _appLock = new();

    private readonly Dictionary<string, (byte[] Data, DateTime Expiry)> _iconCache = new();
    private readonly object _iconLock = new();

    public IReadOnlyList<Shortcut> GetAll()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<Shortcut>();

        lock (_appLock)
        {
            if (_appCache is not null && _appCacheExpiry > DateTime.UtcNow)
                return _appCache;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Get-StartApps | ConvertTo-Json -Compress");

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<Shortcut>();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);

            if (string.IsNullOrWhiteSpace(output))
                return Array.Empty<Shortcut>();

            var apps = ParseStartAppsJson(output);

            lock (_appLock)
            {
                _appCache = apps;
                _appCacheExpiry = DateTime.UtcNow + AppListCacheTtl;
            }

            return apps;
        }
        catch
        {
            return Array.Empty<Shortcut>();
        }
    }

    public Shortcut? GetById(string targetId)
    {
        return GetAll().FirstOrDefault(s =>
            string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Name, targetId, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] GetIcon(string targetId)
    {
        lock (_iconLock)
        {
            if (_iconCache.TryGetValue(targetId, out var cached) && cached.Expiry > DateTime.UtcNow)
                return cached.Data;
        }

        var shortcut = GetById(targetId);
        if (shortcut is null) return Array.Empty<byte>();

        try
        {
            byte[] iconBytes;
            if (shortcut.Id.Contains('!'))
                iconBytes = ExtractUwpIcon(shortcut.Id);
            else
                iconBytes = ExtractWin32Icon(shortcut);

            if (iconBytes.Length > 0)
            {
                lock (_iconLock)
                {
                    _iconCache[targetId] = (iconBytes, DateTime.UtcNow + IconCacheTtl);
                }
            }

            return iconBytes;
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public bool Launch(string targetId)
    {
        var shortcut = GetById(targetId);
        if (shortcut is null) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add($"shell:appsFolder\\{shortcut.Path}");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ExtractWin32Icon(Shortcut shortcut)
    {
        var escapedName = shortcut.Name.Replace("'", "''");
        var script = "Add-Type -AssemblyName System.Drawing; " +
            "$searchDirs = @(" +
            "[IO.Path]::Combine($env:ProgramData, 'Microsoft\\Windows\\Start Menu\\Programs'), " +
            "[IO.Path]::Combine($env:APPDATA, 'Microsoft\\Windows\\Start Menu\\Programs')); " +
            "foreach ($dir in $searchDirs) { " +
            "$lnks = Get-ChildItem $dir -Filter '*.lnk' -Recurse -EA SilentlyContinue | " +
            $"Where-Object {{ $_.BaseName -eq '{escapedName}' }}; " +
            "foreach ($lnk in $lnks) { " +
            "try { $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($lnk.FullName); " +
            "if ($ico) { $bmp = $ico.ToBitmap(); $ms = [IO.MemoryStream]::new(); " +
            "$bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); " +
            "[Convert]::ToBase64String($ms.ToArray()); " +
            "$ms.Dispose(); $bmp.Dispose(); $ico.Dispose(); return } } catch {} } }";

        return RunPowerShellBase64(script, 10000);
    }

    private static byte[] ExtractUwpIcon(string appId)
    {
        var familyName = appId.Split('!')[0].Replace("'", "''");
        var script = $"$pkg = Get-AppxPackage | Where-Object {{ $_.PackageFamilyName -eq '{familyName}' }} | Select-Object -First 1; " +
            "if (-not $pkg) { exit 0 } " +
            "try { $manifest = [xml](Get-Content (Join-Path $pkg.InstallLocation 'AppxManifest.xml')); " +
            "$apps = $manifest.Package.Applications.Application; " +
            "if ($apps -is [array]) { $app = $apps[0] } else { $app = $apps }; " +
            "$logo = $app.VisualElements.Square44x44Logo; " +
            "if (-not $logo) { $logo = $app.VisualElements.Square150x150Logo }; " +
            "if (-not $logo) { exit 0 }; " +
            "$logoFull = Join-Path $pkg.InstallLocation $logo; " +
            "$dir = [IO.Path]::GetDirectoryName($logoFull); " +
            "$base = [IO.Path]::GetFileNameWithoutExtension($logoFull); " +
            "$ext = [IO.Path]::GetExtension($logoFull); " +
            @"$candidates = Get-ChildItem $dir -Filter ""$base*$ext"" -EA SilentlyContinue | " +
            @"Sort-Object { if ($_.Name -match 'scale-(\d+)') { [int]$Matches[1] } else { 0 } } -Descending; " +
            "$target = $null; " +
            "foreach ($c in $candidates) { if (Test-Path $c.FullName) { $target = $c.FullName; break } }; " +
            "if (-not $target -and (Test-Path $logoFull)) { $target = $logoFull }; " +
            "if ($target) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) } " +
            "} catch {}";

        return RunPowerShellBase64(script, 15000);
    }

    private static byte[] RunPowerShellBase64(string script, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            if (proc is null) return Array.Empty<byte>();

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(timeoutMs);

            if (string.IsNullOrEmpty(output)) return Array.Empty<byte>();

            return Convert.FromBase64String(output);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static List<Shortcut> ParseStartAppsJson(string json)
    {
        var apps = new List<Shortcut>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get-StartApps returns array when multiple, object when single
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    AddFromElement(apps, item);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddFromElement(apps, root);
            }
        }
        catch { }

        return apps
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFromElement(List<Shortcut> apps, JsonElement el)
    {
        var name = el.TryGetProperty("Name", out var n) ? n.GetString() : null;
        var appId = el.TryGetProperty("AppID", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(appId)) return;

        // Skip uninstallers and system entries
        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) return;

        apps.Add(new Shortcut
        {
            Id = appId,
            Name = name,
            Path = appId,
        });
    }
}
