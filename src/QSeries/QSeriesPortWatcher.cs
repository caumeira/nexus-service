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

namespace Qos.Service.QSeries;

/// <summary>
/// Keeps the loopback bridge between qos-service and a connected HYTE
/// Q60 / Q80 panel alive — and, after the first successful contact, gets
/// us off USB-FFS adb entirely.
///
/// The Q-series Android shell (`com.nexusqos.panel.qshell`) loads the
/// Qos panel SPA from <c>http://localhost:9400</c>. On the panel side
/// that localhost only reaches qos-service because the host has
/// <c>adb reverse tcp:9400 tcp:9400</c> applied to the attached Q-series
/// display. The reverse is owned by the host's adb-server process — when
/// the daemon exits (because another shell ran an <c>adb</c> command
/// that started its own server, or the user kill-server'd it mid-deploy),
/// the reverse evaporates and the panel WebSocket goes silent.
/// Bench symptom: panel renders, sparklines tick for a few seconds, then
/// values freeze.
///
/// USB-FFS adbd is also fragile: cycling the host adb-server mid-stream
/// can wedge the device-side daemon into <c>offline</c> state, requiring
/// a power-cycle to recover (we can't restart adbd without root). To get
/// out from under that, on the FIRST sight of a Q-series device over USB
/// we:
///   1. Read its LAN IPv4 address via <c>adb shell ip route</c>.
///   2. Send <c>adb -s &lt;serial&gt; tcpip 5555</c> — adbd restarts and
///      starts listening on tcp:5555 in addition to (or instead of) USB.
///   3. Issue <c>adb connect &lt;ip&gt;:5555</c> on the host side so the
///      adb-server now sees the device over TCP.
///   4. Persist <c>(serial, ip)</c> to
///      <c>%ProgramData%\Qos\qseries-transports.json</c>.
/// On subsequent service starts, the watcher reads the file and proactively
/// reconnects each known device — no USB enumeration required. After
/// promotion the panel runs over WiFi; USB serves only as power + the
/// re-bootstrap channel if the device forgets tcpip mode (e.g. after a
/// device reboot).
///
/// The 10 s reverse-port refresh (matches nexus's <c>ADBInterface.startPing</c>
/// from HYTE-ProductTeam/nexus
/// <c>src/main/services/android/ADBInterface.ts</c>) still runs on whatever
/// transport is currently up.
///
/// Idempotent and tolerant: an already-installed reverse is left alone,
/// a missing one is re-applied, an already-promoted device is left alone,
/// and adb-server-not-running / daemon restart / device-detached cases all
/// just turn the next tick into a no-op and recover automatically on the
/// tick after that.
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
    private readonly QSeriesTransportStore _transportStore;

    /// <summary>Last serial we logged "applied reverse on" so the steady-state path stays quiet.</summary>
    private readonly Dictionary<string, bool> _reverseAppliedBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// In-memory mirror of the on-disk transport store. Maps USB serial to
    /// the TCP transport we promoted that device to. Kept on the watcher
    /// so the hot path can check membership without re-reading the file.
    /// Mutated in place — we never replace the reference, so it's readonly.
    /// </summary>
    private readonly Dictionary<string, QSeriesTransportRecord> _promoted;

    public QSeriesPortWatcher(int servicePort)
        : this(servicePort, new QSeriesTransportStore()) { }

    /// <summary>
    /// Test seam: lets tests inject a store that points at a tmp path.
    /// </summary>
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

        // For every persisted (USB → TCP) promotion: if that device's TCP
        // transport isn't already in adb's device list, fire a one-shot
        // `adb connect`. Cheap (a few bytes to the local adb-server, no-op
        // when the transport is already up) and runs every tick so a
        // transient TCP drop self-heals without waiting for USB attach.
        var deviceList = devices.ToList();
        if (_promoted.Count > 0)
        {
            await ReconnectMissingTransportsAsync(deviceList, ct);
            deviceList = (await _client.GetDevicesAsync(ct)).ToList();
        }

        var seenSerials = new HashSet<string>(StringComparer.Ordinal);

        // Promotion pass first: any Q-series device we see as Online over
        // USB and haven't yet promoted gets bootstrapped to TCP mode. We
        // do this before applying reverse so devices coming in over USB
        // for the first time arrive at the TCP transport (where the
        // reverse will stick across USB hiccups) before we install it.
        foreach (var device in deviceList)
        {
            if (string.IsNullOrEmpty(device.Serial)) continue;
            if (device.State != DeviceState.Online) continue;
            if (!IsQSeries(device)) continue;
            if (QSeriesTransport.IsTcpSerial(device.Serial)) continue;
            if (_promoted.ContainsKey(device.Serial)) continue;
            await TryPromoteToTcpAsync(device, ct);
        }

        // The TCP transport from a just-promoted device shows up in
        // adb's device list on its own — we'll catch it on the next tick
        // and install the reverse-port there. No need to re-fetch here.

        foreach (var device in deviceList)
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

    /// <summary>
    /// For each persisted promotion record whose <c>ip:port</c> isn't
    /// already represented in <paramref name="currentDevices"/>, send a
    /// single <c>adb connect</c>. We don't evict failed records here —
    /// the device may be temporarily offline (sleep, network blip) and
    /// a stale-IP record costs nothing on the bench; if the USB transport
    /// reappears later we re-promote with whatever IP the device has now,
    /// which overwrites the entry.
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
    /// First-contact bootstrap: read the device's WiFi IP, send it
    /// <c>tcpip 5555</c>, then have adb-server connect to it over TCP.
    /// Records the (serial, ip) tuple to disk so the next service start
    /// can skip the USB round-trip.
    /// </summary>
    /// <remarks>
    /// adb shell commands here run as the <c>shell</c> user (uid 2000),
    /// which has read access to <c>ip route</c> and the <c>dhcp.*</c>
    /// system properties but cannot itself trigger <c>tcpip</c> — that
    /// has to go through the host adb-server. We shell out to adb.exe
    /// for <c>tcpip</c> the same way we do for <c>start-server</c>.
    /// </remarks>
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

        // Send `adb -s <serial> tcpip 5555`. This restarts adbd on the
        // device in TCP mode; the existing USB transport disappears from
        // adb-server's device list for a couple of seconds.
        if (!RunAdb(adbPath, $"-s {device.Serial} tcpip {QSeriesTransport.DefaultAdbTcpPort}", out var tcpipErr))
        {
            Console.Error.WriteLine(
                $"[qseries-port-watcher] {device.Serial}: tcpip {QSeriesTransport.DefaultAdbTcpPort} failed: {tcpipErr}");
            return;
        }

        // adbd-restart takes a beat. Two seconds is the canonical wait
        // (matches `adb connect`'s own retry behavior).
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
    /// Run <c>adb shell</c> against a device and parse the first LAN IPv4
    /// out of the response. Tries multiple discovery commands — the first
    /// that yields a routable address wins. Returns null if every
    /// strategy fails (device offline mid-promote, no WiFi, link-local
    /// only, …) so the caller can defer until the next tick.
    /// </summary>
    private async Task<System.Net.IPAddress?> DiscoverDeviceIpAsync(DeviceData device, CancellationToken ct)
    {
        // Each entry is one adb shell command. We try them in order; the
        // first that produces a parseable IPv4 wins. `ip route get 1.1.1.1`
        // is preferred because it explicitly picks the interface used for
        // outbound traffic, which for the Q60 is wlan0.
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

    /// <summary>
    /// Synchronous adb.exe shell-out used for <c>tcpip</c>. Uses the same
    /// resolution path as <see cref="TryStartAdbServer"/>. Captures stderr
    /// for the caller's log line. Returns true if adb exited cleanly.
    /// </summary>
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
