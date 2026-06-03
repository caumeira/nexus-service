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
/// Keeps the loopback bridge between nexus-service and a connected HYTE
/// Q60 / Q80 panel alive — and, after the first successful contact, gets
/// us off USB-FFS adb entirely.
///
/// The Q-series Android shell (`com.hellonexus.qshell`) loads the
/// Nexus panel SPA from <c>http://localhost:9400</c>. On the panel side
/// that localhost only reaches nexus-service because the host has
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
/// a power-cycle to recover (we can't restart adbd without root). The
/// watcher has two complementary recovery paths for this:
///
/// 1. <b>Per-tick USB reset for wedged-offline devices</b>. When a serial
///    we've previously identified as Q-series has been in <c>offline</c>
///    state for &gt;30 s, the watcher resolves the device's USB composite
///    parent (<c>USB\VID_xxxx&amp;PID_yyyy\&lt;adb-serial&gt;</c>) and runs
///    <c>pnputil /restart-device</c>. That forces a real USB reset on the
///    device side, which restarts adbd inside the firmware and clears the
///    handshake wedge. Two-minute cooldown per instance keeps a truly
///    unplugged device from getting hammered.
///
/// 2. <b>Bootstrap to adb-over-TCP (WiFi).</b> If the device happens to
///    be on a LAN (HYTE Q60/Q80 firmware can boot WiFi but the touch-less
///    panel can't enter SSID credentials, so this path is dormant on
///    stock units), the watcher reads the device's IP and promotes the
///    transport from USB-FFS to TCP via <c>adb tcpip 5555</c>. After
///    promotion, panel transport runs over WiFi; USB cycling becomes
///    irrelevant. The watcher persists <c>(serial, ip)</c> to
///    <c>%ProgramData%\Nexus\qseries-transports.json</c> so subsequent
///    service starts can reconnect without a USB round-trip.
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

    /// <summary>
    /// How long a Q-series device must stay in adb <c>offline</c> state
    /// before the watcher attempts a USB reset to recover it. Picked to be
    /// longer than the natural USB hiccup window (a deploy that touches
    /// adb-server may leave the device in offline for ~5–15 s while
    /// adb-server re-handshakes) so we don't churn the device when a
    /// natural recovery is already underway.
    /// </summary>
    private static readonly TimeSpan OfflineRecoveryThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum time between consecutive recovery attempts for the same
    /// device. Without a cooldown, a USB device that's stuck offline
    /// (e.g. yanked physically, dead cable) would get hammered with
    /// pnputil restarts every tick. Two minutes is long enough that a
    /// natural recovery from one restart has time to complete.
    /// </summary>
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

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

    /// <summary>
    /// Serials we've ever seen reported by adb with a Q-series model
    /// string. When a serial later shows up as <c>offline</c>, the model
    /// field on the offline-device entry is unreliable, so we can't
    /// re-identify it as Q-series — but we *can* check membership here.
    /// Cleared only when the device is unplugged entirely (not when it
    /// goes offline).
    /// </summary>
    private readonly HashSet<string> _knownQSeriesSerials = new(StringComparer.Ordinal);

    /// <summary>
    /// First time we saw a given serial in <c>offline</c> state on a
    /// continuous run. Reset to (none) when the device returns to
    /// <c>online</c>, or when the device disappears from adb's list.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _offlineSince = new(StringComparer.Ordinal);

    /// <summary>
    /// Last time we ran <c>pnputil /restart-device</c> for a given USB
    /// instance ID. Cooldown is per-instance because that's the granularity
    /// pnputil acts on; if the user unplugs and replugs a different Q-series
    /// device the new instance ID gets its own fresh cooldown.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastRecoveryByInstanceId = new(StringComparer.Ordinal);

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
        // Initial delay matches the other BackgroundServices in nexus-service
        // — gives the rest of the stack a moment to finish boot before we
        // start poking the adb-server (which the nexus host may itself be
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

        var deviceList = devices.ToList();

        // Recovery pass: if any previously-seen Q-series serial is in
        // `offline` state long enough, kick the USB stack on its instance
        // ID to force a real bus reset. That's what unwedges device-side
        // adbd without a power-cycle. Runs first so a successful restart's
        // re-enumeration is visible by the time the reverse-port pass runs.
        await TryRecoverOfflineQSeriesDevicesAsync(deviceList, ct);

        // For every persisted (USB → TCP) promotion: if that device's TCP
        // transport isn't already in adb's device list, fire a one-shot
        // `adb connect`. Cheap (a few bytes to the local adb-server, no-op
        // when the transport is already up) and runs every tick so a
        // transient TCP drop self-heals without waiting for USB attach.
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
            // Once we've seen a serial come online as Q-series, remember
            // it so a future offline-pass can correctly identify it even
            // when adb can't query the model anymore.
            _knownQSeriesSerials.Add(device.Serial);
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

            // A reseat re-enumerates the device with the SAME serial but a NEW
            // adb transport id. The 10 s poll frequently misses the brief
            // offline/absent window, so the serial never leaves seenSerials and
            // the "first sighting" reload flag below is never cleared — leaving
            // qshell stranded on its disconnect splash. Comparing transport ids
            // catches the re-enumeration regardless of poll timing: when it
            // changes, drop the reload flag so EnsureQshellForegroundAsync forces
            // a fresh cold reload against the just-re-applied reverse.
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
        }

        // Forget any serials that disappeared so a re-attach gets a fresh
        // "applied" log line.
        foreach (var key in _reverseAppliedBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _reverseAppliedBySerial.Remove(key);
        }
        // Clear the forced-reload flag for any serial that left the adb list so
        // a physical re-attach gets a fresh cold reload (the device may have come
        // back showing the OEM launcher or a stale splash).
        foreach (var key in _qshellReloadedThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _qshellReloadedThisRun.Remove(key);
        }
        // Drop transport-id memory for serials that left the adb list so a
        // re-attach is treated as a first sighting (which forces a cold reload)
        // rather than a transport-id change against a stale value. (This is also
        // what stops our own reboot's re-enumeration from looking like a fresh
        // reseat — the device left the list during the reboot.) NOTE:
        // _lastQshellRebootBySerial is deliberately NOT cleared here, so the
        // reboot cooldown survives that re-enumeration and can't reboot-loop.
        foreach (var key in _transportIdBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _transportIdBySerial.Remove(key);
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
    /// Walk the current adb device list; for any serial that we've
    /// previously identified as Q-series and is now stuck in
    /// <c>offline</c> state past <see cref="OfflineRecoveryThreshold"/>,
    /// resolve its USB composite parent and run <c>pnputil /restart-device</c>.
    /// That triggers a real USB bus reset, which restarts adbd inside
    /// the device firmware and clears the half-broken handshake state
    /// the daemon ends up in after a mid-stream adb-server cycle.
    /// </summary>
    /// <remarks>
    /// Why pnputil and not <c>adb usb</c>/<c>adb kill-server</c>: those
    /// only touch host-side state. The wedge is on the device side —
    /// adbd has acknowledged the USB-FFS endpoint but won't complete the
    /// handshake. Without root we can't restart adbd from inside, so we
    /// fall back to making the device side observe a USB-level reset.
    /// </remarks>
    private async Task TryRecoverOfflineQSeriesDevicesAsync(IReadOnlyCollection<DeviceData> deviceList, CancellationToken ct)
    {
        // Windows-only: pnputil ships with Windows since Vista, and the
        // wedge symptom (offline adb device after a server cycle) is the
        // one we're actually seeing on the Y70 build PC. Linux/Mac dev
        // hosts would need their own USB-reset path (e.g. usbreset(1)).
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
                // Defer: don't reset _offlineSince — maybe the device
                // re-enumerates with a different name on its own.
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
                // Reset offline tracking so we get a fresh 30 s window
                // if the device fails to recover after the restart.
                _offlineSince.Remove(device.Serial);
            }
            else
            {
                Console.Error.WriteLine(
                    $"[qseries-port-watcher] {device.Serial}: pnputil restart failed: {pnputilOut}");
            }

            // Tiny pause so the next steps of the tick (reverse-port
            // apply, etc.) see at least a partially-reconnected world.
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (TaskCanceledException) { return; }
        }

        // Forget serials that have disappeared from adb entirely (cable
        // unplug). A re-attach gets a fresh offline-since timer.
        foreach (var key in _offlineSince.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _offlineSince.Remove(key);
        }
    }

    /// <summary>
    /// Look up the USB composite-device InstanceId whose tail matches an
    /// adb device serial. HYTE Q60/Q80 composite InstanceIds always end
    /// with the adb serial (<c>USB\VID_xxxx&amp;PID_yyyy\&lt;serial&gt;</c>),
    /// so the match is straightforward. Uses PowerShell + Get-PnpDevice
    /// to avoid hand-rolled SetupAPI P/Invokes.
    /// </summary>
    private static string? TryFindUsbInstanceId(string adbSerial)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (string.IsNullOrEmpty(adbSerial)) return null;
        // Pattern: any USB InstanceId whose tail is "\<serial>". Single
        // quote the literal serial so PowerShell doesn't interpolate.
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
    /// Shell out to <c>pnputil /restart-device "&lt;instanceId&gt;"</c>.
    /// Returns true on exit 0 (or 3010, which pnputil emits for "reboot
    /// recommended" and we don't care about). Captures the output for
    /// the caller's log line. NexusService runs as LocalSystem so it has
    /// the rights pnputil needs without a UAC prompt.
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
            // 0 = success; 3010 = success + reboot recommended (not for
            // /restart-device, but defensive).
            return p.ExitCode == 0 || p.ExitCode == 3010;
        }
        catch (Exception ex)
        {
            output = $"{ex.GetType().Name}: {ex.Message}";
            return false;
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
    /// for the caller's log line. Returns true if adb exited with code 0.
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

    /// <summary>
    /// Activity component to bring to foreground when no panel is visible.
    /// Matches the package + activity names in
    /// <c>hyte-qseries-android/android/app/src/main/AndroidManifest.xml</c>.
    /// </summary>
    private const string QshellComponent = "com.hellonexus.qshell/.MainActivity";

    /// <summary>
    /// String the device's foreground-window dump prints when qshell owns
    /// focus. We only need a substring match — the surrounding line will
    /// look like <c>mCurrentFocus=Window{... com.hellonexus.qshell/...}</c>.
    /// </summary>
    private const string QshellFocusMarker = "com.hellonexus.qshell";

    /// <summary>
    /// Last time we ran <c>am start</c> on a given serial. Throttles the
    /// re-launch path: if we've just restarted qshell, give Android a
    /// couple of ticks to finish bringing it up before we try again. The
    /// foreground check itself is cheap (~30 ms) so we still run it every
    /// tick — only the actual <c>am start</c> is throttled.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellStartBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials for which we've forced a qshell cold-reload since THIS service
    /// instance started. After a service (re)start the qshell that's still
    /// running is bound to the now-dead old instance and sits on its disconnect
    /// splash; a plain foreground check sees it "foreground" and leaves it there.
    /// We force one reload on first sighting this run so it re-navigates to the
    /// panel against the fresh service. Cleared when the device leaves the adb
    /// list so a physical re-attach also gets a fresh forced reload.
    /// </summary>
    private readonly HashSet<string> _qshellReloadedThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Last adb transport id seen for a given Q-series serial. A USB reseat
    /// re-enumerates the device with the same serial but a new transport id;
    /// because the 10 s poll often misses the brief offline window, this is the
    /// only reliable signal that the device was re-plugged. When it changes we
    /// re-arm <see cref="_qshellReloadedThisRun"/> so qshell gets a fresh cold
    /// reload and re-opens the panel instead of sitting on its disconnect splash.
    /// </summary>
    private readonly Dictionary<string, string> _transportIdBySerial = new(StringComparer.Ordinal);

    private static readonly TimeSpan QshellRestartThrottle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Minimum time between reboot-on-reseat actions for the same serial. A
    /// reboot itself re-enumerates the device (new transport id), so without this
    /// a flapping connector — or the reboot's own re-attach — could reboot-loop
    /// it. 2 min comfortably spans a reboot + qshell bootstrap.
    /// </summary>
    private static readonly TimeSpan QshellRebootCooldown = TimeSpan.FromMinutes(2);

    /// <summary>Serial -&gt; last reboot-on-reseat time (anti-loop cooldown). NOT
    /// cleared on detach, so the cooldown survives the reboot's own re-enumeration.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellRebootBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Make sure qshell (<c>com.hellonexus.qshell</c>) is the foreground
    /// activity on a connected Q-series device. We dump the foreground
    /// window via <c>dumpsys window mCurrentFocus</c>; if anything other
    /// than qshell holds focus we run <c>am start</c> to bring it back.
    ///
    /// This is the recovery seam for:
    ///   - first-attach (qshell hasn't been launched yet; the OEM HOME
    ///     launcher is foreground),
    ///   - after a USB reset (qshell got killed when its WebView lost
    ///     transport; the OEM launcher reclaimed foreground),
    ///   - user-triggered <c>am force-stop com.hellonexus.qshell</c>,
    ///   - device reboot.
    /// </summary>
    private async Task EnsureQshellForegroundAsync(DeviceData device, CancellationToken ct)
    {
        // One forced cold reload per service run. EnsureReverseAsync ran just
        // before this in the tick, so the reverse is up. If qshell is still
        // foreground from before a service (re)start it's bound to the dead old
        // instance and shows its disconnect splash — the foreground check below
        // would treat that as "fine" and never reload it. Force-stop + am start
        // re-navigates it to the panel against the fresh service. Gated per
        // serial (and cleared on detach) so it fires once per run.
        if (!_qshellReloadedThisRun.Contains(device.Serial))
        {
            _qshellReloadedThisRun.Add(device.Serial);
            _lastQshellStartBySerial[device.Serial] = DateTimeOffset.UtcNow;
            var reloadReceiver = new ConsoleOutputReceiver();
            try
            {
                // QshellFocusMarker is the bare package name (com.hellonexus.qshell).
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

        // Foreground check. `dumpsys window` is verbose, so let the
        // device-side grep narrow it down — Q-series firmware has busybox
        // grep available (verified bench: HYTE_Q60_Display Android 11).
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
            // qshell already owns focus — nothing to do.
            return;
        }

        // Throttle: if we just kicked am start, give it time to take.
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
    /// A reseat re-enumerated the device (same serial, new adb transport id). A
    /// cold reload (app relaunch) is NOT a strong enough reset for the degraded
    /// USB-FFS link a quick reseat leaves behind — bench-proven: qshell relaunches
    /// into the flaky link and its bootstrap never completes, so it sits on the
    /// splash. Only a device reboot reliably clears the device-side adbd/FFS
    /// gadget. So reboot directly, throttled per serial so a flapping connector
    /// (or the reboot's own re-attach) can't reboot-loop it. After the reboot the
    /// device re-attaches and the first-sighting cold reload in
    /// <see cref="EnsureQshellForegroundAsync"/> reconnects qshell on the fresh
    /// link. Returns true if a reboot was issued — the caller then skips this
    /// tick's reverse/qshell passes for the now-rebooting device.
    /// </summary>
    private async Task<bool> TryRebootOnReseatAsync(DeviceData device, string lastTransportId, string transportId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot)
            && now - lastReboot < QshellRebootCooldown)
        {
            // Within cooldown: don't reboot again. Fall back to a cold-reload
            // re-arm so qshell at least retries against the re-applied reverse.
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
