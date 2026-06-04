using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using AdvancedSharpAdbClient.Models;
using AdvancedSharpAdbClient.Receivers;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.QSeries;

/// <summary>
/// Keeps <c>adb reverse tcp:{port} tcp:{port}</c> alive on an attached HYTE
/// Q60 / Q80 panel so its Android shell (<c>com.hellonexus.qshell</c>) can load
/// the Nexus panel SPA from <c>http://localhost:{port}</c>. The reverse is owned
/// by the host adb-server; if that server dies the reverse evaporates and the
/// panel WebSocket goes silent.
///
/// USB-FFS adbd is fragile: cycling the host adb-server mid-stream can wedge the
/// device daemon into <c>offline</c>, which can't be cleared from the host without
/// root. Recovery: per-tick <c>pnputil /restart-device</c> for a device stuck
/// offline (a USB-level reset that restarts device-side adbd), plus an optional
/// promote to adb-over-TCP when the panel has a LAN IP (dormant on stock touch-less
/// units that can't enter WiFi creds), persisted to
/// <c>%ProgramData%\Nexus\qseries-transports.json</c>.
///
/// Every pass is idempotent: an already-applied reverse / already-promoted device
/// is left alone, and adb-server-down / device-detached turn into no-ops that
/// recover on a later tick.
/// </summary>
public sealed class QSeriesPortWatcher : BackgroundService
{
    /// <summary>
    /// Keep-alive cadence. 10 s is shorter than the panel's reconnect backoff cap
    /// (30 s) so a dropped reverse is re-applied before a user-visible stall.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>ro.product.model</c> values that mark a Q-series panel. THICC_Q_Series is
    /// a legacy pre-release model name, kept so a firmware downgrade still matches.
    /// </summary>
    private static readonly HashSet<string> QSeriesModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "HYTE_Q60_Display",
        "HYTE_Q80_Display",
        "THICC_Q_Series",
    };

    /// <summary>
    /// How long a Q-series serial must stay adb-<c>offline</c> before a USB reset.
    /// Longer than the natural ~5–15 s offline blip while adb-server re-handshakes,
    /// so a recovery already underway isn't churned.
    /// </summary>
    private static readonly TimeSpan OfflineRecoveryThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum gap between USB resets for one instance — stops a physically-dead
    /// device from being reset every tick. 2 min lets one reset's recovery complete.
    /// </summary>
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    private readonly int _servicePort;
    private readonly string _localSpec;
    private readonly string _remoteSpec;
    private readonly AdbClient _client;
    private readonly QSeriesTransportStore _transportStore;

    /// <summary>Serials with an applied reverse, so the steady-state path stays quiet.</summary>
    private readonly Dictionary<string, bool> _reverseAppliedBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials whose reverse was force-refreshed (remove + re-add) this run. The
    /// first tick after a (re)start tears the reverse down and re-adds it to clear a
    /// soft wedge — an entry <c>adb reverse --list</c> still shows but that passes no
    /// traffic; later ticks keep the quiet rebind. Cleared on adb-server death and
    /// on detach.
    /// </summary>
    private readonly HashSet<string> _reverseRefreshedThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// In-memory mirror of the on-disk transport store (USB serial → promoted TCP
    /// transport). Mutated in place, never reassigned, so readonly.
    /// </summary>
    private readonly Dictionary<string, QSeriesTransportRecord> _promoted;

    /// <summary>
    /// Serials ever seen as Q-series. An <c>offline</c> entry's model field is
    /// unreliable, so re-identify by membership here. Cleared only on unplug.
    /// </summary>
    private readonly HashSet<string> _knownQSeriesSerials = new(StringComparer.Ordinal);

    /// <summary>
    /// First time a serial was seen <c>offline</c> this run. Cleared when it returns
    /// online or leaves the adb list.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _offlineSince = new(StringComparer.Ordinal);

    /// <summary>
    /// Last <c>pnputil /restart-device</c> time per USB instance id (the granularity
    /// pnputil acts on). A different replugged device gets its own cooldown.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastRecoveryByInstanceId = new(StringComparer.Ordinal);

    public QSeriesPortWatcher(int servicePort)
        : this(servicePort, new QSeriesTransportStore()) { }

    /// <summary>Test seam: inject a store pointing at a tmp path.</summary>
    public QSeriesPortWatcher(int servicePort, QSeriesTransportStore transportStore)
    {
        _servicePort = servicePort;
        _localSpec = $"tcp:{servicePort}";
        _remoteSpec = $"tcp:{servicePort}";
        _client = new AdbClient();
        _transportStore = transportStore;
        _promoted = _transportStore.Load();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the stack finish boot before poking the adb-server (the
        // host may be booting the bundled adb.exe alongside us).
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
                // SocketException "connection refused 127.0.0.1:5037" = adb-server
                // not running (no Q-series attached, or not booted yet) — the normal
                // idle state. Log and retry next tick.
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
            // adb-server is dead. AdvancedSharpAdbClient talks to an existing server
            // over TCP and won't spawn one; shell out to `adb start-server` and retry
            // once (a still-failing start is picked up next tick).
            _reverseAppliedBySerial.Clear();
            _reverseRefreshedThisRun.Clear();
            if (TryStartAdbServer())
            {
                try
                {
                    devices = await _client.GetDevicesAsync(ct);
                }
                catch
                {
                    // start-server returned but the socket still refused — retry next tick.
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
            _reverseRefreshedThisRun.Clear();
            throw;
        }

        var deviceList = devices.ToList();

        // Recovery pass first so a successful USB-reset's re-enumeration is visible
        // to the reverse-port pass below.
        await TryRecoverOfflineQSeriesDevicesAsync(deviceList, ct);

        // One-shot `adb connect` per persisted promotion not already in the device
        // list. Cheap, and self-heals a transient TCP drop without a USB attach.
        if (_promoted.Count > 0)
        {
            await ReconnectMissingTransportsAsync(deviceList, ct);
            deviceList = (await _client.GetDevicesAsync(ct)).ToList();
        }

        var seenSerials = new HashSet<string>(StringComparer.Ordinal);

        // Promote any not-yet-promoted online USB Q-series to TCP before applying the
        // reverse, so it lands on the transport that survives USB hiccups.
        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            if (!IsQSeries(device)) continue;
            // Remember the serial so a later offline pass can identify it without the
            // (then-unreliable) model field.
            _knownQSeriesSerials.Add(device.Serial);
            if (QSeriesTransport.IsTcpSerial(device.Serial)) continue;
            if (_promoted.ContainsKey(device.Serial)) continue;
            await TryPromoteToTcpAsync(device, ct);
        }

        // A just-promoted device's TCP transport appears on the next tick; no
        // re-fetch here.

        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            if (!IsQSeries(device)) continue;

            seenSerials.Add(device.Serial);

            // A reseat re-enumerates with the same serial but a new transport id. The
            // 10 s poll often misses the brief offline window, so a transport-id change
            // is the reliable reseat signal: on change, reboot to reset the degraded
            // USB-FFS link (a cold reload alone doesn't clear it).
            var transportId = device.TransportId;
            if (!string.IsNullOrEmpty(transportId))
            {
                var reseated = _transportIdBySerial.TryGetValue(device.Serial, out var lastTransportId)
                    && lastTransportId != transportId;
                _transportIdBySerial[device.Serial] = transportId;
                if (reseated && await TryRebootOnReseatAsync(device, lastTransportId!, transportId, ct))
                    continue; // device is rebooting; skip the reverse/qshell passes this tick
            }

            await EnsureReverseAsync(device, ct);
            await EnsureQshellForegroundAsync(device, ct);
            await TryEscalateRebootAsync(device, ct);
        }

        // Per-serial state for serials that left the adb list. A re-attach re-logs
        // the applied reverse and force-refreshes it again.
        foreach (var key in _reverseAppliedBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseAppliedBySerial.Remove(key);
        }
        foreach (var key in _reverseRefreshedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseRefreshedThisRun.Remove(key);
        }
        // A re-attach gets a fresh cold reload (it may return on the OEM launcher or a
        // stale splash).
        foreach (var key in _qshellReloadedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _qshellReloadedThisRun.Remove(key);
        }
        // Re-arm the cold-reload grace anchor on detach. _escalationRebootedThisRun is
        // deliberately NOT cleared here — like _lastQshellRebootBySerial it must
        // survive our reboot's re-enumeration so a still-stranded panel isn't rebooted
        // twice.
        foreach (var key in _coldReloadAtBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _coldReloadAtBySerial.Remove(key);
        }
        // Drop transport-id memory on detach so a re-attach is a fresh first sighting,
        // not a transport-id change against a stale value (this also stops our reboot's
        // re-enumeration from looking like a reseat).
        foreach (var key in _transportIdBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _transportIdBySerial.Remove(key);
        }
    }

    /// <summary>
    /// <c>adb connect</c> each persisted promotion not already present. Failed
    /// records aren't evicted — the device may be briefly offline, and a stale IP is
    /// overwritten when the USB transport re-promotes.
    /// </summary>
    private async Task ReconnectMissingTransportsAsync(IReadOnlyCollection<DeviceData> currentDevices, CancellationToken ct)
    {
        var present = new HashSet<string>(
            currentDevices.Select(d => d.Serial ?? string.Empty).Where(s => s.Length > 0),
            StringComparer.Ordinal);
        foreach (var record in _promoted.Values.ToList())
        {
            var tcpSerial = $"{record.IpAddress}:{record.Port}";
            if (present.Contains(tcpSerial)) continue;
            try
            {
                var result = await _client.ConnectAsync(record.IpAddress, record.Port, ct);
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] reconnect {tcpSerial} ({record.Model}): {result?.Trim() ?? "ok"}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] reconnect {tcpSerial} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// For any known-Q-series serial stuck <c>offline</c> past
    /// <see cref="OfflineRecoveryThreshold"/>, resolve its USB composite parent and
    /// run <c>pnputil /restart-device</c> — a USB-level reset that restarts adbd in
    /// firmware and clears the handshake wedge. Host-side <c>adb</c> can't: the wedge
    /// is device-side and adbd can't be restarted without root.
    /// </summary>
    private async Task TryRecoverOfflineQSeriesDevicesAsync(IReadOnlyCollection<DeviceData> deviceList, CancellationToken ct)
    {
        // Windows-only: pnputil is the available USB-reset path, and the wedge is the
        // one seen on the Y70 host. Other hosts would need usbreset(1) etc.
        if (!OperatingSystem.IsWindows()) return;

        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            present.Add(device.Serial);
            if (!_knownQSeriesSerials.Contains(device.Serial)) continue;

            if (device.State == DeviceState.Online)
            {
                _offlineSince.Remove(device.Serial);
                continue;
            }
            if (device.State != DeviceState.Offline) continue;

            if (!_offlineSince.TryGetValue(device.Serial, out var since))
            {
                _offlineSince[device.Serial] = now;
                continue;
            }
            var offlineFor = now - since;
            if (offlineFor < OfflineRecoveryThreshold) continue;

            var instanceId = TryFindUsbInstanceId(device.Serial);
            if (instanceId is null)
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: offline for {offlineFor.TotalSeconds:F0}s but no matching USB instance id found");
                // Defer; the device may re-enumerate under a different name.
                continue;
            }

            if (_lastRecoveryByInstanceId.TryGetValue(instanceId, out var last)
                && now - last < RecoveryCooldown)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: offline for {offlineFor.TotalSeconds:F0}s, running pnputil /restart-device {instanceId}");
            _lastRecoveryByInstanceId[instanceId] = now;
            if (RunPnputilRestartDevice(instanceId, out var pnputilOut))
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: pnputil restart succeeded; awaiting re-enumeration ({pnputilOut})");
                // Fresh 30 s window if it fails to recover after the restart.
                _offlineSince.Remove(device.Serial);
            }
            else
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: pnputil restart failed: {pnputilOut}");
            }

            // Brief pause so the rest of the tick sees a partially-reconnected world.
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (TaskCanceledException) { return; }
        }

        // A re-attach gets a fresh offline-since timer.
        foreach (var key in _offlineSince.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _offlineSince.Remove(key);
        }
    }

    /// <summary>
    /// USB composite InstanceId whose tail is the adb serial
    /// (<c>USB\VID_xxxx&amp;PID_yyyy\&lt;serial&gt;</c>), via Get-PnpDevice.
    /// </summary>
    private static string? TryFindUsbInstanceId(string adbSerial)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(adbSerial)) return null;
        // Single-quote the serial so PowerShell doesn't interpolate it.
        var psScript =
            "Get-PnpDevice -Class USB " +
            "| Where-Object { $_.InstanceId -like '*\\" + adbSerial + "' } " +
            "| Select-Object -First 1 -ExpandProperty InstanceId";
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psScript}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            if (!p.WaitForExit(5_000))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrEmpty(stdout) ? null : stdout;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>pnputil /restart-device</c>. True on exit 0 or 3010. NexusService runs as
    /// LocalSystem, so no UAC prompt.
    /// </summary>
    private static bool RunPnputilRestartDevice(string instanceId, out string output)
    {
        output = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/restart-device \"{instanceId}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(true); } catch { }
                output = "timed out after 15s";
                return false;
            }
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            var stderr = p.StandardError.ReadToEnd().Trim();
            output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout} | err: {stderr}";
            // 3010 = success + reboot-recommended (defensive; not emitted by /restart-device).
            return p.ExitCode == 0 || p.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            output = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// First-contact bootstrap: read the device WiFi IP, <c>adb tcpip 5555</c>, then
    /// <c>adb connect</c> over TCP; persist (serial, ip) so the next start skips the
    /// USB round-trip. <c>tcpip</c> must go through the host adb-server (the shell
    /// user can't trigger it), so it's shelled out like start-server.
    /// </summary>
    private async Task TryPromoteToTcpAsync(DeviceData device, CancellationToken ct)
    {
        var ip = await DiscoverDeviceIpAsync(device, ct);
        if (ip is null)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: cannot discover LAN IP; skipping TCP promote (will retry next tick)");
            return;
        }

        var adbPath = ResolveAdbPath();
        if (adbPath is null)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: adb.exe not found; cannot run tcpip promote");
            return;
        }

        // Restarts adbd in TCP mode; the USB transport drops from the list briefly.
        if (!RunAdb(adbPath, $"-s {device.Serial} tcpip {QSeriesTransport.DefaultAdbTcpPort}", out var tcpipErr))
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: tcpip {QSeriesTransport.DefaultAdbTcpPort} failed: {tcpipErr}");
            return;
        }

        // adbd restart settles in ~2 s (matches adb connect's own retry).
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (TaskCanceledException) { return; }

        try
        {
            var result = await _client.ConnectAsync(ip.ToString(), QSeriesTransport.DefaultAdbTcpPort, ct);
            Console.Error.WriteLine(
                $"[qseries-port-watcher] promoted {device.Serial} ({device.Model}) -> tcp:{ip}:{QSeriesTransport.DefaultAdbTcpPort}: {result?.Trim() ?? "ok"}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] connect {ip}:{QSeriesTransport.DefaultAdbTcpPort} failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var record = new QSeriesTransportRecord(
            Model: device.Model ?? string.Empty,
            IpAddress: ip.ToString(),
            Port: QSeriesTransport.DefaultAdbTcpPort,
            PromotedAt: DateTimeOffset.UtcNow);
        _promoted[device.Serial] = record;
        _transportStore.Save(_promoted);
    }

    /// <summary>
    /// First routable LAN IPv4 from a series of <c>adb shell</c> probes; null if none
    /// (offline mid-promote, no WiFi, link-local only).
    /// </summary>
    private async Task<System.Net.IPAddress?> DiscoverDeviceIpAsync(DeviceData device, CancellationToken ct)
    {
        // First command yielding a parseable IPv4 wins; `ip route get` picks the
        // outbound interface (wlan0 on the Q60).
        string[] commands =
        {
            "ip route get 1.1.1.1",
            "getprop dhcp.wlan0.ipaddress",
            "ip -4 addr show wlan0",
        };
        foreach (var cmd in commands)
        {
            var receiver = new ConsoleOutputReceiver();
            try
            {
                await _client.ExecuteShellCommandAsync(device, cmd, receiver, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: shell `{cmd}` failed: {ex.GetType().Name}");
                continue;
            }
            var ip = QSeriesTransport.ParseLanIPv4(receiver.ToString());
            if (ip is not null) return ip;
        }
        return null;
    }

    /// <summary>Synchronous adb.exe shell-out (used for <c>tcpip</c>). True on exit 0.</summary>
    private static bool RunAdb(string adbPath, string arguments, out string errorOutput)
    {
        errorOutput = string.Empty;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(8_000))
            {
                try { p.Kill(true); } catch { }
                errorOutput = "timed out after 8s";
                return false;
            }
            if (p.ExitCode != 0)
            {
                errorOutput = $"exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            errorOutput = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool IsQSeries(DeviceData device)
    {
        // The adb device-line `model:` field is the only reliable Q-series
        // discriminator; state + serial don't disambiguate.
        var model = device.Model ?? string.Empty;
        return QSeriesModels.Contains(model.Replace(" ", "_"));
    }

    /// <summary>
    /// Shell out to <c>adb start-server</c> when the server is down (kill-server,
    /// fresh boot, crash) — AdvancedSharpAdbClient won't spawn it. adb.exe resolved
    /// from PATH, then the Android SDK platform-tools dir.
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
        // PATH first (build-pc convention + user platform-tools).
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
        // Then %LOCALAPPDATA%\Android\Sdk\platform-tools (Android Studio default).
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

    /// <summary>qshell package/activity; matches its AndroidManifest.xml.</summary>
    private const string QshellComponent = "com.hellonexus.qshell/.MainActivity";

    /// <summary>Substring of <c>dumpsys window mCurrentFocus</c> when qshell owns focus.</summary>
    private const string QshellFocusMarker = "com.hellonexus.qshell";

    /// <summary>
    /// Last <c>am start</c> time per serial. Throttles re-launch; the foreground
    /// check itself is cheap and runs every tick.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellStartBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials force-reloaded this run. A qshell left running across a service
    /// (re)start is bound to the dead instance and shows its splash, which the
    /// foreground check reads as "fine"; force-stop + am start re-navigates it to the
    /// panel. Cleared on detach.
    /// </summary>
    private readonly HashSet<string> _qshellReloadedThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Last adb transport id per serial. A change (same serial) is the reliable reseat
    /// signal the 10 s poll otherwise misses; see TickAsync.
    /// </summary>
    private readonly Dictionary<string, string> _transportIdBySerial = new(StringComparer.Ordinal);

    private static readonly TimeSpan QshellRestartThrottle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Minimum gap between reboots per serial. A reboot re-enumerates the device (new
    /// transport id), so without this a flapping connector — or the reboot's own
    /// re-attach — could reboot-loop. 2 min spans reboot + qshell bootstrap.
    /// </summary>
    private static readonly TimeSpan QshellRebootCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Wait after the first-sighting cold reload before the one-shot escalation reboot
    /// — enough for qshell to relaunch and reconnect (~3 ticks).
    /// </summary>
    private static readonly TimeSpan EscalationGrace = TimeSpan.FromSeconds(30);

    /// <summary>Serial → last reboot time (anti-loop cooldown). NOT cleared on detach,
    /// so it survives the reboot's own re-enumeration.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellRebootBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Time of this run's first-sighting cold reload; anchors <see cref="EscalationGrace"/>.
    /// Cleared on detach (a re-attach re-arms it).
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _coldReloadAtBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials escalation-rebooted this run — bounds it to one reboot per run so a
    /// dead panel can't loop. NOT cleared on detach (survives the reboot's
    /// re-enumeration); a fresh service start re-allows.
    /// </summary>
    private readonly HashSet<string> _escalationRebootedThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Keep qshell foreground: dump <c>mCurrentFocus</c>; if something else holds
    /// focus, <c>am start</c> it. Covers first-attach (OEM launcher), post-USB-reset
    /// kill, user force-stop, and reboot.
    /// </summary>
    private async Task EnsureQshellForegroundAsync(DeviceData device, CancellationToken ct)
    {
        // One forced cold reload per run: a qshell still foreground from before a
        // service (re)start is bound to the dead instance and shows its splash; the
        // foreground check below reads that as "fine". force-stop + am start
        // re-navigates it to the fresh service.
        if (!_qshellReloadedThisRun.Contains(device.Serial))
        {
            _qshellReloadedThisRun.Add(device.Serial);
            var reloadNow = DateTimeOffset.UtcNow;
            _lastQshellStartBySerial[device.Serial] = reloadNow;
            // Anchor the escalation grace window from this cold reload.
            _coldReloadAtBySerial[device.Serial] = reloadNow;
            var reloadReceiver = new ConsoleOutputReceiver();
            try
            {
                await _client.ExecuteShellCommandAsync(device, $"am force-stop {QshellFocusMarker}", reloadReceiver, ct);
                await _client.ExecuteShellCommandAsync(device, $"am start -n {QshellComponent}", reloadReceiver, ct);
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: forced qshell reload on first sighting this run (reconnect to fresh service)");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: forced qshell reload failed: {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }

        // Device-side grep narrows the verbose dumpsys (busybox grep present on
        // Q-series Android 11).
        var focusReceiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(device, "dumpsys window | grep mCurrentFocus", focusReceiver, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: foreground check failed: {ex.GetType().Name}");
            return;
        }
        if (focusReceiver.ToString().Contains(QshellFocusMarker, StringComparison.Ordinal))
        {
            return;
        }

        // Throttle: give a just-issued am start time to take.
        var now = DateTimeOffset.UtcNow;
        if (_lastQshellStartBySerial.TryGetValue(device.Serial, out var last)
            && now - last < QshellRestartThrottle)
        {
            return;
        }
        _lastQshellStartBySerial[device.Serial] = now;

        var startReceiver = new ConsoleOutputReceiver();
        try
        {
            await _client.ExecuteShellCommandAsync(device, $"am start -n {QshellComponent}", startReceiver, ct);
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: qshell not in foreground, ran am start ({startReceiver.ToString().Trim()})");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: am start qshell failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A reseat (same serial, new transport id) leaves a degraded USB-FFS link a cold
    /// reload can't fix — qshell relaunches into it and never completes its bootstrap.
    /// Reboot to reset device-side adbd/FFS; the post-reboot first-sighting cold reload
    /// reconnects qshell. Throttled per serial. Returns true when a reboot was issued
    /// (caller then skips this tick's reverse/qshell passes).
    /// </summary>
    private async Task<bool> TryRebootOnReseatAsync(DeviceData device, string lastTransportId, string transportId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot)
            && now - lastReboot < QshellRebootCooldown)
        {
            // Within cooldown: re-arm a cold reload instead of rebooting again.
            _qshellReloadedThisRun.Remove(device.Serial);
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: transport id {lastTransportId} -> {transportId} (reseat) but a reboot is within cooldown; re-arming cold reload instead");
            return false;
        }

        _lastQshellRebootBySerial[device.Serial] = now;
        try
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: transport id {lastTransportId} -> {transportId} (USB re-enumeration / reseat); rebooting device to reset USB-FFS");
            await _client.RebootAsync(device, ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: reseat reboot failed: {ex.GetType().Name}: {ex.Message}; re-arming cold reload");
            _qshellReloadedThisRun.Remove(device.Serial);
            return false;
        }
    }

    /// <summary>
    /// Backstop the reseat path misses: a host *service* restart cold-reloads qshell,
    /// but if the abrupt kill wedged the USB-FFS tunnel qshell re-pings a dead tunnel
    /// and latches to its splash with no reseat to trigger
    /// <see cref="TryRebootOnReseatAsync"/>. A device reboot is the only reliable clear.
    ///
    /// No liveness probe first: both available signals are unusable. The loopback
    /// socket reads ESTABLISHED on the splash too (adbd accepts qshell's
    /// <c>localhost:{port}</c> dial locally regardless of whether the forward works),
    /// and reading the framebuffer to check the screen is destructive — the ~3.6 MB
    /// pull over the shared USB-FFS gadget stalls the reverse tunnel and freezes a
    /// healthy panel. So after the grace window we reboot once, unconditionally:
    /// needed in the stranded case, a single ~40 s reboot in the rarer
    /// already-recovered case.
    ///
    /// One reboot per run (<see cref="_escalationRebootedThisRun"/>); shares
    /// <see cref="_lastQshellRebootBySerial"/> with the reseat path so they can't
    /// double-reboot across the re-enumeration.
    /// </summary>
    private async Task TryEscalateRebootAsync(DeviceData device, CancellationToken ct)
    {
        // No anchor → the cold reload hasn't run this run; nothing to escalate.
        if (!_coldReloadAtBySerial.TryGetValue(device.Serial, out var reloadedAt)) return;
        if (_escalationRebootedThisRun.Contains(device.Serial)) return;

        var now = DateTimeOffset.UtcNow;
        var sinceReload = now - reloadedAt;
        if (sinceReload < EscalationGrace) return;

        if (_lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot)
            && now - lastReboot < QshellRebootCooldown)
        {
            // A reseat reboot just fired — let it play out.
            return;
        }

        try
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: {sinceReload.TotalSeconds:F0}s after cold reload; rebooting once to clear any USB-FFS wedge from the host restart");
            await _client.RebootAsync(device, ct);
            // Record the reboot only after it's issued; if RebootAsync throws (stale
            // transport id mid-re-enumeration) leave the flags unset so the next tick
            // retries instead of latching the run as already-rebooted.
            _lastQshellRebootBySerial[device.Serial] = now;
            _escalationRebootedThisRun.Add(device.Serial);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: escalation reboot failed: {ex.GetType().Name}: {ex.Message}; will retry next tick");
        }
    }

    private async Task EnsureReverseAsync(DeviceData device, CancellationToken ct)
    {
        // First sighting this run: remove + re-add the reverse to clear a soft wedge
        // (a tcp:{port} entry still listed but passing no traffic) at the moment a
        // (re)start creates it. Steady-state ticks keep the quiet rebind below; a hard
        // wedge this can't clear is TryEscalateRebootAsync's job.
        if (!_reverseRefreshedThisRun.Contains(device.Serial))
        {
            _reverseRefreshedThisRun.Add(device.Serial);
            _reverseAppliedBySerial.Remove(device.Serial); // re-log the apply below
            try
            {
                await _client.RemoveReverseForwardAsync(device, _remoteSpec, ct);
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: force-refreshed reverse on first sighting this run (removed {_remoteSpec})");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // No prior reverse (fresh boot) throws here — harmless; the create installs it.
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: reverse pre-refresh remove no-op: {ex.GetType().Name}");
            }
        }

        // allowRebind: true is idempotent — re-binds an existing reverse instead of
        // failing "cannot rebind existing socket" on later ticks. The mapping is always
        // tcp:{port} -> tcp:{port}, so a rebind is a no-op for other consumers.
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
            // Re-apply runs next tick; forget the applied flag so the recovery re-logs.
            _reverseAppliedBySerial.Remove(device.Serial);
            Console.Error.WriteLine(
                $"[qseries-port-watcher] reverse apply failed for {device.Serial}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
