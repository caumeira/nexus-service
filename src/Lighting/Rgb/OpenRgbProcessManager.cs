using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Qos.Service.Lighting.Rgb;

/// <summary>
/// Owns the OpenRGB-headless subprocess. Locates the binary in
/// <see cref="AppContext.BaseDirectory"/>/openrgb (where the publish output drops
/// the bundled headless build), and starts/stops it on demand.
///
/// Auto-restart with exponential backoff: 1s → 2s → 4s → 8s → 16s, capped at 30s.
/// Backoff resets after a process has been running for 60+ seconds.
///
/// AOT-safe — pure System.Diagnostics.Process, no reflection.
/// </summary>
public sealed class OpenRgbProcessManager : IDisposable
{
    public const int DefaultPort = 6742;

    private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly string _exePath;
    private Process? _proc;
    private DateTime _startedUtc;
    private TimeSpan _backoff = InitialBackoff;
    private CancellationTokenSource? _supervisorCts;
    private bool _disposed;

    public OpenRgbProcessManager(string? overrideExePath = null)
    {
        _exePath = overrideExePath ?? ResolveDefaultPath();
    }

    public bool IsAvailable => (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && File.Exists(_exePath);

    /// <summary>
    /// Kill any OpenRGB-headless processes left behind by a crashed or force-killed
    /// service. Called on startup before spawning a new instance.
    /// </summary>
    public static void CleanupOrphans()
    {
        try
        {
            var name = OperatingSystem.IsWindows() ? "OpenRGB-headless" : "openrgb-headless";
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1000);
                    Console.Error.WriteLine($"[openrgb-proc] killed orphan PID {proc.Id}");
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
    }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _proc is { HasExited: false };
            }
        }
    }

    public string ExePath => _exePath;

    /// <summary>
    /// Resolved path: <c>{AppContext.BaseDirectory}/openrgb/OpenRGB-headless.exe</c>.
    /// </summary>
    public static string ResolveDefaultPath()
    {
        var name = OperatingSystem.IsWindows() ? "OpenRGB-headless.exe" : "OpenRGB-headless";
        return Path.Combine(AppContext.BaseDirectory, "openrgb", name);
    }

    /// <summary>
    /// Service-owned OpenRGB config directory. Defaults to
    /// <c>%LOCALAPPDATA%\Qos\openrgb-config</c> on Windows so we
    /// don't collide with any user-installed OpenRGB.
    /// </summary>
    public static string ResolveConfigDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.GetTempPath();
        }

        return Path.Combine(baseDir, "Qos", "openrgb-config");
    }

    /// <summary>
    /// Start the subprocess if it isn't already running. Idempotent. Safe to
    /// call from any thread.
    /// </summary>
    public void Start()
    {
        if (!IsAvailable)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_proc is { HasExited: false })
            {
                return;
            }

            CleanupProcessLocked();
            CleanupOrphans();

            // Use a service-owned config directory so we don't collide with any
            // user-installed OpenRGB. Lives under %LOCALAPPDATA%\Qos on Windows.
            var configDir = ResolveConfigDir();
            try
            { Directory.CreateDirectory(configDir); }
            catch { /* will fail loudly when OpenRGB itself tries */ }

            var psi = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--server");
            psi.ArgumentList.Add("--server-port");
            psi.ArgumentList.Add(DefaultPort.ToString());
            psi.ArgumentList.Add("--noautoconnect");
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(configDir);
            psi.ArgumentList.Add("--loglevel");
            psi.ArgumentList.Add("error");

            try
            {
                var proc = Process.Start(psi);
                if (proc is null)
                {
                    return;
                }

                _proc = proc;
                _startedUtc = DateTime.UtcNow;

                // Drain stdout/stderr so the OS pipe buffers don't fill up
                _ = Task.Run(() => DrainStreamAsync(proc.StandardOutput, "stdout"));
                _ = Task.Run(() => DrainStreamAsync(proc.StandardError, "stderr"));

                // Replace the previous CTS so Stop() can cancel only the current
                // supervisor — and dispose the old one to avoid leaks.
                var oldCts = _supervisorCts;
                _supervisorCts = new CancellationTokenSource();
                try
                { oldCts?.Dispose(); }
                catch { }

                var token = _supervisorCts.Token;
                _ = Task.Run(() => SupervisorAsync(proc, token));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[openrgb-proc] failed to launch {_exePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Stop the subprocess. Idempotent. Sends a kill (no clean shutdown signal —
    /// the headless server holds no on-disk state, so kill is safe).
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _supervisorCts;
            _supervisorCts = null;
            CleanupProcessLocked();
            _backoff = InitialBackoff;
        }
        try
        { cts?.Cancel(); }
        catch { }
        try
        { cts?.Dispose(); }
        catch { }
    }

    private void CleanupProcessLocked()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(500);
            }
        }
        catch { /* swallow — best effort */ }
        try
        { _proc?.Dispose(); }
        catch { }
        _proc = null;
    }

    private async Task SupervisorAsync(Process proc, CancellationToken ct)
    {
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        var uptime = DateTime.UtcNow - _startedUtc;
        Console.Error.WriteLine($"[openrgb-proc] subprocess exited code={proc.ExitCode} after {uptime.TotalSeconds:F0}s");

        // Reset backoff if it lived long enough to be considered "stable"
        TimeSpan backoff;
        lock (_lock)
        {
            if (uptime > StableUptime)
            {
                _backoff = InitialBackoff;
            }

            backoff = _backoff;
            // Bump for next time
            _backoff = TimeSpan.FromMilliseconds(Math.Min(_backoff.TotalMilliseconds * 2, MaxBackoff.TotalMilliseconds));
        }

        try
        { await Task.Delay(backoff, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Re-check disposed AND cancellation under the lock so we don't race with Stop().
        // Start() itself re-checks, but bailing here saves a process spawn we'd just kill.
        bool shouldRestart;
        lock (_lock)
        {
            shouldRestart = !_disposed && !ct.IsCancellationRequested;
        }
        if (shouldRestart)
        {
            Start();
        }
    }

    private static async Task DrainStreamAsync(StreamReader reader, string label)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (IsKnownNoise(line))
                {
                    continue;
                }

                Console.Error.WriteLine($"[openrgb-proc/{label}] {line}");
            }
        }
        catch { /* pipe closed on shutdown */ }
    }

    /// <summary>
    /// Suppress upstream OpenRGB log lines that are expected on every run and
    /// don't indicate a real problem. This keeps our service stderr useful.
    /// </summary>
    private static bool IsKnownNoise(string line)
    {
        // Client disconnect — fires every time the bridge closes the TCP socket.
        if (line.Contains("recv_select failed receiving magic"))
        {
            return true;
        }
        // PawnIO probes — only relevant for SMBus motherboards we don't ship the .bin files for.
        if (line.Contains("PawnIO initialization aborted"))
        {
            return true;
        }
        // Optional sound card detector that always fails on machines without an AE-5.
        if (line.Contains("[Creative SoundBlaster AE-5]"))
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
