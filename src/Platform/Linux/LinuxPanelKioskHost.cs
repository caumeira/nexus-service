using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Panel;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Hosts promoted-monitor panel kiosks on Linux: one Chromium-family
/// <c>--app --kiosk</c> window per active display assignment, spawned into
/// the user's graphical session (the setpriv root-daemon pattern shared
/// with the dashboard launcher and screencast helper). Counterpart of
/// nexus-overlay's MonitorKioskManager (Windows) and the overlay-helper
/// KioskController (macOS).
///
/// Placement caveat: Wayland gives clients no protocol to target a specific
/// output, so the kiosk fullscreens on the compositor-chosen monitor -
/// exact on single-monitor rigs, best-effort on multi-monitor. The DRM
/// topology also carries no positions, so there is nothing to translate a
/// displayId into screen coordinates with from the daemon side.
///
/// Each kiosk gets its own --user-data-dir under XDG_RUNTIME_DIR so the
/// spawned pid IS the browser process (no delegation to a running
/// instance) and Kill(tree) closes exactly that window.
/// </summary>
public sealed class LinuxPanelKioskHost : IDisposable
{
    private static int _servicePort = 9400;
    public static void Configure(int servicePort) => _servicePort = servicePort;

    private const int RapidFailureWindowSeconds = 30;
    private const int MaxRapidFailures = 3;

    private readonly TokenService _tokens;
    private readonly PanelDeviceRegistry _registry;
    private readonly object _lock = new();
    private readonly Dictionary<string, Kiosk> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rapidFailures = new(StringComparer.Ordinal);
    private bool _disposed;
    private bool _noBrowserLogged;

    private sealed class Kiosk
    {
        public Kiosk(Process process, string deviceId, DateTime spawnedUtc)
        {
            Process = process;
            DeviceId = deviceId;
            SpawnedUtc = spawnedUtc;
        }

        public Process Process { get; }
        public string DeviceId { get; }
        public DateTime SpawnedUtc { get; }
    }

    public LinuxPanelKioskHost(TokenService tokens, PanelDeviceRegistry registry)
    {
        _tokens = tokens;
        _registry = registry;
    }

    /// <summary>
    /// Re-reads the active assignment set from the registry under the host
    /// lock - racing callers each see fresh state, so the last reconcile to
    /// run reflects the newest store snapshot (no stale-args ordering race).
    /// </summary>
    public void Reconcile()
    {
        if (!OperatingSystem.IsLinux())
            return;
        lock (_lock)
        {
            if (_disposed)
                return;
            var assignments = _registry.ListAssignments()
                .Select(a => (a.DisplayId, a.PanelDeviceId))
                .ToList();

            // Drop exited entries so the open pass below respawns them.
            foreach (var key in _running.Keys.ToList())
            {
                if (_running[key].Process.HasExited)
                {
                    try { _running[key].Process.Dispose(); } catch { }
                    _running.Remove(key);
                }
            }

            var runningMap = _running.ToDictionary(kv => kv.Key, kv => kv.Value.DeviceId, StringComparer.Ordinal);
            var (toClose, toOpen) = Diff(runningMap, assignments);
            // A demoted or re-bound display starts from a clean slate: the
            // rapid-failure cap only spans one continuous assignment.
            foreach (var displayId in toClose)
                _rapidFailures.Remove(displayId);
            foreach (var capped in _rapidFailures.Keys.ToList())
            {
                if (!assignments.Any(a => string.Equals(a.DisplayId, capped, StringComparison.Ordinal)))
                    _rapidFailures.Remove(capped);
            }
            foreach (var displayId in toClose)
                CloseKiosk(displayId);
            foreach (var (displayId, deviceId) in toOpen)
                SpawnKiosk(displayId, deviceId);
        }
    }

    /// <summary>Pure reconcile diff: close what is no longer desired (or
    /// changed device), open what is desired but not running.</summary>
    internal static (List<string> ToClose, List<(string DisplayId, string DeviceId)> ToOpen) Diff(
        IReadOnlyDictionary<string, string> running,
        IReadOnlyList<(string DisplayId, string DeviceId)> desired)
    {
        var desiredById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (displayId, deviceId) in desired)
            desiredById[displayId] = deviceId;

        var toClose = new List<string>();
        foreach (var (displayId, deviceId) in running)
        {
            if (!desiredById.TryGetValue(displayId, out var want) || !string.Equals(want, deviceId, StringComparison.Ordinal))
                toClose.Add(displayId);
        }

        var toOpen = new List<(string, string)>();
        foreach (var (displayId, deviceId) in desiredById)
        {
            if (!running.TryGetValue(displayId, out var have) || !string.Equals(have, deviceId, StringComparison.Ordinal))
                toOpen.Add((displayId, deviceId));
        }
        return (toClose, toOpen);
    }

    private void CloseKiosk(string displayId)
    {
        if (!_running.Remove(displayId, out var kiosk))
            return;
        try
        {
            if (!kiosk.Process.HasExited)
                kiosk.Process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] close failed display={displayId}: {ex.Message}");
        }
        finally
        {
            try { kiosk.Process.Dispose(); } catch { }
        }
        Console.Error.WriteLine($"[panel-kiosk] closed display={displayId}");
    }

    private void SpawnKiosk(string displayId, string deviceId)
    {
        if (_rapidFailures.GetValueOrDefault(displayId) > MaxRapidFailures)
            return;

        var browser = LinuxBrowsers.FindChromium();
        if (browser is null)
        {
            if (!_noBrowserLogged)
            {
                _noBrowserLogged = true;
                Console.Error.WriteLine("[panel-kiosk] no Chromium-family browser found; monitor panels need chromium/chrome/brave/edge installed");
            }
            return;
        }

        // Token rides in argv like the macOS helper's --token; /proc/*/cmdline
        // exposure on multi-user boxes is a known follow-up (short-TTL
        // bootstrap token), not solved here.
        var url = $"http://localhost:{_servicePort}/panel/{Uri.EscapeDataString(deviceId)}?token={Uri.EscapeDataString(_tokens.Token)}";
        var args = new List<string>();
        // Same Wayland forcing as the dashboard launcher: Chromium otherwise
        // tries X11 and dies on a pure-Wayland login.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            args.Add("--ozone-platform=wayland");
        args.Add($"--app={url}");
        args.Add("--kiosk");
        args.Add($"--user-data-dir={ProfileDir(deviceId)}");
        args.Add("--no-first-run");
        args.Add("--noerrdialogs");
        args.Add("--disable-session-crashed-bubble");

        var (file, wrapped) = LinuxSession.WrapSpawnAsSessionUser(browser, args);
        // No stream redirection: Chromium logs to stderr for its whole
        // lifetime, and an undrained 64KB pipe would eventually block its
        // write() and freeze the kiosk. Inherit the service's stdio instead.
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in wrapped)
            psi.ArgumentList.Add(a);

        try
        {
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine($"[panel-kiosk] spawn returned null display={displayId}");
                return;
            }
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnKioskExited(displayId);
            _running[displayId] = new Kiosk(proc, deviceId, DateTime.UtcNow);
            Console.Error.WriteLine($"[panel-kiosk] opened display={displayId} device={deviceId} via {browser} pid {proc.Id}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] spawn failed display={displayId}: {ex.Message}");
        }
    }

    private void OnKioskExited(string displayId)
    {
        bool respawn;
        lock (_lock)
        {
            if (_disposed)
                return;
            if (!_running.TryGetValue(displayId, out var kiosk) || !kiosk.Process.HasExited)
                return;
            _running.Remove(displayId);
            try { kiosk.Process.Dispose(); } catch { }

            if (DateTime.UtcNow - kiosk.SpawnedUtc < TimeSpan.FromSeconds(RapidFailureWindowSeconds))
            {
                var failures = _rapidFailures.GetValueOrDefault(displayId) + 1;
                _rapidFailures[displayId] = failures;
                if (failures > MaxRapidFailures)
                {
                    Console.Error.WriteLine($"[panel-kiosk] giving up on display={displayId} after {failures} rapid failures (turn the panel off and on to retry)");
                    return;
                }
            }
            else
            {
                _rapidFailures.Remove(displayId);
            }
            respawn = _registry.ListAssignments()
                .Any(a => string.Equals(a.DisplayId, displayId, StringComparison.Ordinal));
        }
        if (!respawn)
            return;
        Console.Error.WriteLine($"[panel-kiosk] display={displayId} exited; respawning");
        // 2s crash backoff, same as the overlay host launchers.
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2000);
            Reconcile();
        });
    }

    private static string ProfileDir(string deviceId)
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var baseDir = string.IsNullOrEmpty(runtimeDir) ? Path.GetTempPath() : runtimeDir;
        // Not pre-created: the browser runs as the session user and creates
        // it itself; a root-owned pre-creation would be unwritable.
        return Path.Combine(baseDir, "nexus-kiosk", deviceId);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var displayId in _running.Keys.ToList())
                CloseKiosk(displayId);
        }
    }
}
