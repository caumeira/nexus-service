using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using Microsoft.Extensions.Hosting;

namespace Qos.Service.QSeries;

/// <summary>
/// Keeps the loopback bridge between qos-service and a connected HYTE
/// Q60 / Q80 panel alive.
///
/// The Q-series Android shell (`com.nexusqos.panel.qshell`) loads the
/// Qos panel SPA from <c>http://localhost:9400</c>. On the panel side
/// that localhost only reaches qos-service because the Y70 host has
/// <c>adb reverse tcp:9400 tcp:9400</c> applied to the attached Q-series
/// USB display. The reverse is owned by the host's adb-server process —
/// when the daemon exits (e.g. because another shell ran an <c>adb</c>
/// command that started its own server and the old one gave up the
/// socket), the reverse evaporates and the panel's multiplex WebSocket
/// goes silent. Bench symptom: panel renders, sparklines tick for a few
/// seconds, then values freeze.
///
/// Nexus solves this with a 10 s ping that re-applies the reverse on
/// every tick (HYTE-ProductTeam/nexus
/// <c>src/main/services/android/ADBInterface.ts</c>). This watcher does
/// the equivalent: it polls connected devices via the adb-server
/// protocol every <see cref="PollInterval"/>, and for each device whose
/// model matches a Q-series identifier
/// (<c>HYTE_Q60_Display</c> / <c>HYTE_Q80_Display</c> /
/// <c>THICC_Q_Series</c>) it ensures <c>adb reverse tcp:{servicePort}
/// tcp:{servicePort}</c> is installed.
///
/// Idempotent and tolerant: an already-installed reverse is left alone,
/// a missing one is re-applied, and adb-server-not-running / daemon
/// restart / device-detached cases all just turn the next tick into a
/// no-op and recover automatically on the tick after that.
/// </summary>
public sealed class QSeriesPortWatcher : BackgroundService
{
    /// <summary>
    /// Cadence of the keep-alive ping. Matches nexus's
    /// <c>ADBInterface.startPing</c> 10 s interval, which is short
    /// enough that the panel's <c>useMultiplexSocket</c> exponential
    /// backoff (caps at 30 s on the SPA side) reliably reconnects
    /// before the next user-visible stall.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Device-model strings reported by <c>getprop ro.product.model</c>
    /// over adb. HYTE_Q60_Display and HYTE_Q80_Display are bench-
    /// verified; THICC_Q_Series is the older internal model name some
    /// pre-release units shipped with (covered defensively so a
    /// firmware downgrade doesn't accidentally fall off the watcher).
    /// </summary>
    private static readonly HashSet<string> QSeriesModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "HYTE_Q60_Display",
        "HYTE_Q80_Display",
        "THICC_Q_Series",
    };

    private readonly int _servicePort;
    private readonly string _localSpec;
    private readonly string _remoteSpec;
    private readonly AdbClient _client;

    /// <summary>Last serial we logged "applied reverse on" so the steady-state path stays quiet.</summary>
    private readonly Dictionary<string, bool> _reverseAppliedBySerial = new(StringComparer.Ordinal);

    public QSeriesPortWatcher(int servicePort)
    {
        _servicePort = servicePort;
        _localSpec = $"tcp:{servicePort}";
        _remoteSpec = $"tcp:{servicePort}";
        _client = new AdbClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay matches the other BackgroundServices in qos-service
        // — gives the rest of the stack a moment to finish boot before we
        // start poking the adb-server (which the qos host may itself be
        // booting alongside us via the bundled adb.exe).
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Most common failure here is `SocketException: Connection
                // refused 127.0.0.1:5037` when the adb-server isn't running
                // (e.g. the Q-series isn't attached, or the user hasn't
                // booted the bundled adb daemon yet). That's the normal
                // idle state — log once at info level via Console.Error,
                // then sleep and try again next tick.
                Console.Error.WriteLine($"[qseries-port-watcher] tick failed: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        IEnumerable<DeviceData> devices;
        try
        {
            devices = await _client.GetDevicesAsync(ct);
        }
        catch (SocketException) when (!ct.IsCancellationRequested)
        {
            // adb-server is dead. AdvancedSharpAdbClient talks to an
            // already-running server via TCP — it does NOT auto-spawn
            // one. Boot one ourselves by shelling out to `adb.exe
            // start-server`, then retry once. We don't loop because if
            // start-server itself fails the next tick will pick up
            // anyway (10 s later) — the goal here is recovery within a
            // single tick, not exhausting retries on a wedged host.
            _reverseAppliedBySerial.Clear();
            if (TryStartAdbServer())
            {
                try
                {
                    devices = await _client.GetDevicesAsync(ct);
                }
                catch
                {
                    // start-server reported success but the socket still
                    // refused — let it ride until next tick.
                    throw;
                }
            }
            else
            {
                throw;
            }
        }
        catch
        {
            _reverseAppliedBySerial.Clear();
            throw;
        }

        var seenSerials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in devices)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            if (!IsQSeries(device)) continue;

            seenSerials.Add(device.Serial);
            await EnsureReverseAsync(device, ct);
        }

        // Forget any serials that disappeared so a re-attach gets a fresh
        // "applied" log line.
        foreach (var key in _reverseAppliedBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseAppliedBySerial.Remove(key);
        }
    }

    private static bool IsQSeries(DeviceData device)
    {
        // adb's device-line `model:` field is the most reliable identifier
        // here. State + Serial alone don't disambiguate a Q60 from any
        // other adb-attached device the host happens to have.
        var model = device.Model ?? string.Empty;
        return QSeriesModels.Contains(model.Replace(" ", "_"));
    }

    /// <summary>
    /// Best-effort spawn of `adb start-server`. AdvancedSharpAdbClient
    /// itself talks to an already-running server on tcp:5037 — when
    /// the server is dead (kill-server, fresh boot, crash) we bring it
    /// back by shelling out to the standard adb CLI. We resolve adb.exe
    /// from PATH first, then from common install locations Windows users
    /// typically have (the Android SDK platform-tools dir under
    /// LocalAppData, and Microsoft's bundled OneDrive Android SDK).
    /// </summary>
    private static bool TryStartAdbServer()
    {
        var adbPath = ResolveAdbPath();
        if (adbPath is null)
        {
            Console.Error.WriteLine("[qseries-port-watcher] adb.exe not found in PATH or common locations; cannot start adb-server");
            return false;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "start-server",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(8_000))
            {
                try { p.Kill(true); } catch { }
                Console.Error.WriteLine("[qseries-port-watcher] adb start-server timed out after 8s");
                return false;
            }
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"[qseries-port-watcher] adb start-server exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
                return false;
            }
            Console.Error.WriteLine("[qseries-port-watcher] adb-server (re)started");
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[qseries-port-watcher] adb start-server threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string? ResolveAdbPath()
    {
        // 1) PATH lookup (covers the build-pc skill convention and any
        //    user-installed platform-tools).
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
        }
        // 2) %LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe — Android
        //    Studio's default install path on Windows.
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                var androidSdk = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
                if (File.Exists(androidSdk)) return androidSdk;
            }
        }
        return null;
    }

    private async Task EnsureReverseAsync(DeviceData device, CancellationToken ct)
    {
        // CreateReverseForwardAsync(allowRebind: true) is the idempotent
        // form: if the reverse already exists it gets silently re-bound;
        // if it's missing it gets created. With `false` the adb-server
        // returns "cannot rebind existing socket" on the second-and-later
        // ticks because the previous tick already installed it. Our
        // mapping is always tcp:{servicePort} -> tcp:{servicePort}, so a
        // rebind onto the same target is a no-op for any other consumer.
        try
        {
            await _client.CreateReverseForwardAsync(device, _localSpec, _remoteSpec, true, ct);
            if (!_reverseAppliedBySerial.TryGetValue(device.Serial, out _))
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] reverse applied: {device.Serial} ({device.Model}) {_localSpec} -> {_remoteSpec}");
                _reverseAppliedBySerial[device.Serial] = true;
            }
        }
        catch (Exception ex)
        {
            // Lost reverse on next tick → re-apply path will run again.
            // Forget the "applied" flag so we log when the reverse comes
            // back, not silently swallow another miss.
            _reverseAppliedBySerial.Remove(device.Serial);
            Console.Error.WriteLine(
                $"[qseries-port-watcher] reverse apply failed for {device.Serial}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
