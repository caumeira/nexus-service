using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Nexus.Service.Diagnostics.Storage;

/// <summary>
/// Linux SMART/NVMe health via smartmontools' smartctl CLI: `smartctl --scan
/// --json` enumerates devices, then `smartctl -a -j &lt;device&gt;` reads each
/// one. Cached on the same refresh cadence as the Windows
/// LibreHardwareMonitor-backed <see cref="SmartHealthMonitor"/>. smartctl
/// missing (empty --scan output) reports Supported=false; a scan that runs
/// but finds zero or unreadable devices still reports Supported=true with an
/// empty/partial drive list, matching the Windows monitor's "the read
/// mechanism works" meaning of Supported. Never throws.
/// </summary>
public sealed class LinuxSmartHealthMonitor
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ForceRefreshFloor = TimeSpan.FromSeconds(5);
    private const int ShellTimeoutMs = 8000;

    private readonly Func<string> _scanJson;
    private readonly Func<LinuxSmartctlParser.ScanEntry, string> _deviceJson;

    private readonly object _lock = new();
    private readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();
    private readonly HashSet<string> _warnedDrives = new();
    private SmartSnapshot _cached = new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };
    private bool _hasSnapshot;

    public LinuxSmartHealthMonitor()
        : this(
            () => Nexus.Service.Platform.ShellExecutor.Run("smartctl", ShellTimeoutMs, "--scan", "--json"),
            entry => string.IsNullOrEmpty(entry.Type)
                ? Nexus.Service.Platform.ShellExecutor.Run("smartctl", ShellTimeoutMs, "-a", "-j", entry.Name)
                : Nexus.Service.Platform.ShellExecutor.Run("smartctl", ShellTimeoutMs, "-a", "-j", "-d", entry.Type, entry.Name))
    {
    }

    // Injected command seams for tests (no real smartctl needed).
    internal LinuxSmartHealthMonitor(Func<string> scanJson, Func<LinuxSmartctlParser.ScanEntry, string> deviceJson)
    {
        _scanJson = scanJson;
        _deviceJson = deviceJson;
    }

    public SmartSnapshot Snapshot()
    {
        if (ShouldRefresh(RefreshInterval))
        {
            Refresh();
        }
        lock (_lock)
        {
            return _cached;
        }
    }

    public void ForceRefresh()
    {
        if (ShouldRefresh(ForceRefreshFloor))
        {
            Refresh();
        }
    }

    private bool ShouldRefresh(TimeSpan floor)
    {
        lock (_lock)
        {
            return !_hasSnapshot || _sinceRefresh.Elapsed >= floor;
        }
    }

    // Not lock-protected across the smartctl sweep itself, only the small
    // state mutations within it - a slow or unresponsive device must not
    // block a concurrent Snapshot() caller behind the whole scan+per-device
    // shell-out chain. Two callers racing past ShouldRefresh both do the
    // real work and race harmlessly to publish _cached; the loser's result
    // is simply overwritten, never corrupted.
    private void Refresh()
    {
        try
        {
            var scanJson = _scanJson();
            if (string.IsNullOrWhiteSpace(scanJson))
            {
                // smartctl not installed, or --scan produced nothing at all.
                // Keep the previous cached snapshot rather than clobbering it
                // with empty data if we already had one.
                lock (_lock)
                {
                    if (!_hasSnapshot)
                    {
                        _cached = new SmartSnapshot { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };
                    }
                }
                return;
            }

            var scanEntries = LinuxSmartctlParser.ParseScan(scanJson);
            var drives = new List<SmartDriveInfo>();
            foreach (var entry in scanEntries)
            {
                try
                {
                    var deviceJson = _deviceJson(entry);
                    var drive = LinuxSmartctlParser.ParseDevice(deviceJson, entry);
                    if (drive is not null)
                    {
                        drives.Add(drive);
                    }
                }
                catch (Exception ex)
                {
                    WarnOnce(entry.Name, ex);
                }
            }
            lock (_lock)
            {
                _cached = new SmartSnapshot { Supported = true, Drives = drives };
            }
        }
        catch (Exception ex)
        {
            Nexus.Service.Platform.ServiceLog.Warn($"[smart] refresh failed: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _sinceRefresh.Restart();
                _hasSnapshot = true;
            }
        }
    }

    private void WarnOnce(string driveName, Exception ex)
    {
        lock (_lock)
        {
            if (!_warnedDrives.Add(driveName))
            {
                return;
            }
        }
        Nexus.Service.Platform.ServiceLog.Warn($"[smart] failed reading drive {driveName}: {ex.Message}");
    }
}
