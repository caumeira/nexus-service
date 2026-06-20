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
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Platform;

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
    /// MediaTek USB vendor id - the Q60/Q80 panel's SoC, seen when the Android
    /// panel is in adb mode. Generic to MediaTek, so it's a presence hint used
    /// alongside the Q-series cooler's own VID/PID, never an identity proof.
    /// </summary>
    private const int MediaTekAdbVendorId = 0x0E8D;

    /// <summary>
    /// How long a Q-series serial must stay adb-<c>offline</c> before a USB reset.
    /// Longer than the natural ~5–15 s offline blip while adb-server re-handshakes,
    /// so a recovery already underway isn't churned.
    /// </summary>
    private static readonly TimeSpan OfflineRecoveryThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Minimum gap between USB resets for one instance - stops a physically-dead
    /// device from being reset every tick. 2 min lets one reset's recovery complete.
    /// </summary>
    private static readonly TimeSpan RecoveryCooldown = TimeSpan.FromMinutes(2);

    private readonly int _servicePort;
    private readonly string _localSpec;
    private readonly string _remoteSpec;
    private readonly AdbClient _client;
    private readonly QSeriesTransportStore _transportStore;
    private readonly HardwarePresence _presence;

    /// <summary>
    /// Panel registry, read-only here, to check whether the Q-series panel has
    /// re-contacted the service (its record's <c>LastSeenAt</c>) after a host
    /// restart - the liveness gate for the escalation reboot.
    /// </summary>
    private readonly PanelDeviceRegistry _panelDevices;

    /// <summary>Serials with an applied reverse, so the steady-state path stays quiet.</summary>
    private readonly Dictionary<string, bool> _reverseAppliedBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials whose reverse was force-refreshed (remove + re-add) this run. The
    /// first tick after a (re)start tears the reverse down and re-adds it to clear a
    /// soft wedge - an entry <c>adb reverse --list</c> still shows but that passes no
    /// traffic; later ticks keep the quiet rebind. Cleared on adb-server death and
    /// on detach.
    /// </summary>
    private readonly HashSet<string> _reverseRefreshedThisRun = new(StringComparer.Ordinal);

    /// <summary>Serials we've already logged "no LAN IP" for, so a USB-only unit
    /// doesn't repeat that line every tick. Cleared on adb-server death.</summary>
    private readonly HashSet<string> _lanIpUnavailableLogged = new(StringComparer.Ordinal);

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

    public QSeriesPortWatcher(int servicePort, HardwarePresence presence, PanelDeviceRegistry panelDevices)
        : this(servicePort, presence, panelDevices, new QSeriesTransportStore()) { }

    /// <summary>Test seam: inject a store pointing at a tmp path.</summary>
    public QSeriesPortWatcher(int servicePort, HardwarePresence presence, PanelDeviceRegistry panelDevices, QSeriesTransportStore transportStore)
    {
        _servicePort = servicePort;
        _presence = presence;
        _panelDevices = panelDevices;
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
                // The clean-host idle case (no Q-series ⇒ no adb-server) is gated out
                // in TickAsync before any adb call, so reaching here means a Q-series
                // is present but its adb path genuinely failed - a real error.
                ServiceLog.Error($"[qseries-port-watcher] tick failed: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Don't probe adb unless a Q-series unit is plausibly attached: its cooler
        // (VID_3402&PID_0400/0403) or the panel's MediaTek adb interface (VID_0E8D)
        // on USB, or a TCP-promoted device we still maintain. Otherwise
        // GetDevicesAsync spawns and polls a dead adb-server every tick on every
        // host that has no Q-series at all.
        if (_promoted.Count == 0
            && !_presence.UsbPresent(QSeriesCoolerProtocol.VendorId, QSeriesCoolerProtocol.Q60ProductId, QSeriesCoolerProtocol.Q80ProductId)
            && !_presence.UsbPresent(MediaTekAdbVendorId))
        {
            return;
        }

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
            _lanIpUnavailableLogged.Clear();
            if (TryStartAdbServer())
            {
                try
                {
                    devices = await _client.GetDevicesAsync(ct);
                }
                catch
                {
                    // start-server returned but the socket still refused - retry next tick.
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
            _lanIpUnavailableLogged.Clear();
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
            // USB-FFS link (re-launching qshell alone doesn't clear it).
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
            await SyncDeviceClockAsync(device, ct);
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
        // A re-attach is a fresh first sighting (it may return on the OEM launcher or
        // a stale splash, needing an am start).
        foreach (var key in _qshellFirstSeenThisRun.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _qshellFirstSeenThisRun.Remove(key);
        }
        // Re-arm the grace anchor on detach. _escalationRebootedThisRun is
        // deliberately NOT cleared here - like _lastQshellRebootBySerial it must
        // survive our reboot's re-enumeration so a still-stranded panel isn't rebooted
        // twice.
        foreach (var key in _firstSeenAtBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _firstSeenAtBySerial.Remove(key);
        }
        // Drop transport-id memory on detach so a re-attach is a fresh first sighting,
        // not a transport-id change against a stale value (this also stops our reboot's
        // re-enumeration from looking like a reseat).
        foreach (var key in _transportIdBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _transportIdBySerial.Remove(key);
        }
        // Re-sync the clock on a re-attach (a reboot may have reset the device clock).
        foreach (var key in _lastClockSyncBySerial.Keys.Where(k => !seenSerials.Contains(k)).ToList())
        {
            _lastClockSyncBySerial.Remove(key);
        }
    }

    /// <summary>
    /// <c>adb connect</c> each persisted promotion not already present. Failed
    /// records aren't evicted - the device may be briefly offline, and a stale IP is
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
                ServiceLog.Info(
                    $"[qseries-port-watcher] reconnect {tcpSerial} ({record.Model}): {result?.Trim() ?? "ok"}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] reconnect {tcpSerial} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// For any known-Q-series serial stuck <c>offline</c> past
    /// <see cref="OfflineRecoveryThreshold"/>, resolve its USB composite parent and
    /// run <c>pnputil /restart-device</c> - a USB-level reset that restarts adbd in
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
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: offline for {offlineFor.TotalSeconds:F0}s but no matching USB instance id found");
                // Defer; the device may re-enumerate under a different name.
                continue;
            }

            if (_lastRecoveryByInstanceId.TryGetValue(instanceId, out var last)
                && now - last < RecoveryCooldown)
            {
                continue;
            }

            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: offline for {offlineFor.TotalSeconds:F0}s, running pnputil /restart-device {instanceId}");
            _lastRecoveryByInstanceId[instanceId] = now;
            if (RunPnputilRestartDevice(instanceId, out var pnputilOut))
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: pnputil restart succeeded; awaiting re-enumeration ({pnputilOut})");
                // Fresh 30 s window if it fails to recover after the restart.
                _offlineSince.Remove(device.Serial);
            }
            else
            {
                ServiceLog.Info(
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
            // A USB-only Q-series has no LAN IP and never will, so this fires every
            // tick - log it once per serial instead of flooding.
            if (_lanIpUnavailableLogged.Add(device.Serial))
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: no LAN IP; staying on USB (suppressing repeat)");
            }
            return;
        }

        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: adb.exe not found; cannot run tcpip promote");
            return;
        }

        // Restarts adbd in TCP mode; the USB transport drops from the list briefly.
        if (!RunAdb(adbPath, $"-s {device.Serial} tcpip {QSeriesTransport.DefaultAdbTcpPort}", out var tcpipErr))
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: tcpip {QSeriesTransport.DefaultAdbTcpPort} failed: {tcpipErr}");
            return;
        }

        // adbd restart settles in ~2 s (matches adb connect's own retry).
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (TaskCanceledException) { return; }

        try
        {
            var result = await _client.ConnectAsync(ip.ToString(), QSeriesTransport.DefaultAdbTcpPort, ct);
            ServiceLog.Info(
                $"[qseries-port-watcher] promoted {device.Serial} ({device.Model}) -> tcp:{ip}:{QSeriesTransport.DefaultAdbTcpPort}: {result?.Trim() ?? "ok"}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
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
                ServiceLog.Info(
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
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
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
    /// fresh boot, crash) - AdvancedSharpAdbClient won't spawn it. adb.exe resolved
    /// via <see cref="Nexus.Service.Panel.AdbLocator"/> (bundled copy, then PATH/SDK).
    /// </summary>
    private static bool TryStartAdbServer()
    {
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            ServiceLog.Info("[qseries-port-watcher] adb.exe not found in PATH or common locations; cannot start adb-server");
            return false;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = "start-server",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit(8_000))
            {
                try { p.Kill(true); } catch { }
                ServiceLog.Info("[qseries-port-watcher] adb start-server timed out after 8s");
                return false;
            }
            if (p.ExitCode != 0)
            {
                ServiceLog.Info($"[qseries-port-watcher] adb start-server exit {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}");
                return false;
            }
            ServiceLog.Info("[qseries-port-watcher] adb-server (re)started");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[qseries-port-watcher] adb start-server threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
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
    /// Serials whose first sighting this run has been handled (escalation grace
    /// anchored, qshell ensured foreground). A qshell left running across a
    /// service restart keeps its WebView mounted; its SPA reconnects the socket
    /// on its own, so this run no longer force-stops it. Cleared on detach.
    /// </summary>
    private readonly HashSet<string> _qshellFirstSeenThisRun = new(StringComparer.Ordinal);

    /// <summary>
    /// Last adb transport id per serial. A change (same serial) is the reliable reseat
    /// signal the 10 s poll otherwise misses; see TickAsync.
    /// </summary>
    private readonly Dictionary<string, string> _transportIdBySerial = new(StringComparer.Ordinal);

    private static readonly TimeSpan QshellRestartThrottle = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Minimum gap between reboots per serial. A reboot re-enumerates the device (new
    /// transport id), so without this a flapping connector - or the reboot's own
    /// re-attach - could reboot-loop. 2 min spans reboot + qshell bootstrap.
    /// </summary>
    private static readonly TimeSpan QshellRebootCooldown = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Grace after first sighting this run before the liveness-gated escalation
    /// reboot. Must exceed the slowest legitimate reconnect: a cold qshell
    /// bootstrap pulls the SPA + ~56 assets over the marginal USB-FFS link and
    /// takes ~2 min (per the panel recovery guide), far longer than the soft
    /// case (qshell stays up, socket reconnects in seconds). Sized above that so
    /// a cold boot / relaunch is never preempted; only a panel still silent past
    /// it is rebooted.
    /// </summary>
    private static readonly TimeSpan EscalationGrace = TimeSpan.FromSeconds(180);

    /// <summary>Serial → last reboot time (anti-loop cooldown). NOT cleared on detach,
    /// so it survives the reboot's own re-enumeration.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastQshellRebootBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Time a serial was first seen this run; anchors <see cref="EscalationGrace"/>.
    /// Cleared on detach (a re-attach re-arms it).
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _firstSeenAtBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Serials escalation-rebooted this run - bounds it to one reboot per run so a
    /// dead panel can't loop. NOT cleared on detach (survives the reboot's
    /// re-enumeration); a fresh service start re-allows.
    /// </summary>
    private readonly HashSet<string> _escalationRebootedThisRun = new(StringComparer.Ordinal);

    /// <summary>Re-push the host clock to the panel this often; covers RTC drift
    /// without spamming set-time every tick.</summary>
    private static readonly TimeSpan ClockSyncInterval = TimeSpan.FromMinutes(30);

    /// <summary>Serial -> last device-clock sync time. Cleared on detach so a
    /// re-attach (which may have reset the clock on reboot) re-syncs at once.</summary>
    private readonly Dictionary<string, DateTimeOffset> _lastClockSyncBySerial = new(StringComparer.Ordinal);

    /// <summary>
    /// Keep qshell foreground: dump <c>mCurrentFocus</c>; if something else holds
    /// focus, <c>am start</c> it. Covers first-attach (OEM launcher), post-USB-reset
    /// kill, user force-stop, and reboot. Does NOT force-stop a running qshell: it
    /// keeps its WebView mounted across a host restart and its SPA reconnects on
    /// its own, so a force-stop would gratuitously bounce the panel through the
    /// splash. First sighting this run only anchors the escalation grace.
    /// </summary>
    private async Task EnsureQshellForegroundAsync(DeviceData device, CancellationToken ct)
    {
        if (!_qshellFirstSeenThisRun.Contains(device.Serial))
        {
            _qshellFirstSeenThisRun.Add(device.Serial);
            _firstSeenAtBySerial[device.Serial] = DateTimeOffset.UtcNow;
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
            ServiceLog.Info(
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
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: qshell not in foreground, ran am start ({startReceiver.ToString().Trim()})");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: am start qshell failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// A reseat (same serial, new transport id) leaves a degraded USB-FFS link that
    /// strands qshell. Reboot to reset device-side adbd/FFS; the post-reboot first
    /// sighting re-anchors the grace and ensures qshell is foreground. Throttled per
    /// serial. Returns true when a reboot was issued (caller then skips this tick's
    /// reverse/qshell passes).
    /// </summary>
    private async Task<bool> TryRebootOnReseatAsync(DeviceData device, string lastTransportId, string transportId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot)
            && now - lastReboot < QshellRebootCooldown)
        {
            // Within cooldown: re-arm first-sighting handling instead of rebooting again.
            _qshellFirstSeenThisRun.Remove(device.Serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: transport id {lastTransportId} -> {transportId} (reseat) but a reboot is within cooldown; re-arming first-sighting handling instead");
            return false;
        }

        _lastQshellRebootBySerial[device.Serial] = now;
        try
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: transport id {lastTransportId} -> {transportId} (USB re-enumeration / reseat); rebooting device to reset USB-FFS");
            await _client.RebootAsync(device, ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: reseat reboot failed: {ex.GetType().Name}: {ex.Message}; re-arming first-sighting handling");
            _qshellFirstSeenThisRun.Remove(device.Serial);
            return false;
        }
    }

    /// <summary>
    /// The Q-series panel is sealed with no NTP, so its RTC drifts (seen stuck
    /// years off), and the panel renders its clock widget from the device's own
    /// wall clock. Push the host's time + IANA timezone to it via <c>cmd
    /// alarm</c>, which the shell user can call without root. Re-synced on first
    /// sighting and every <see cref="ClockSyncInterval"/> for drift.
    /// </summary>
    private async Task SyncDeviceClockAsync(DeviceData device, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastClockSyncBySerial.TryGetValue(device.Serial, out var last)
            && now - last < ClockSyncInterval)
        {
            return;
        }
        _lastClockSyncBySerial[device.Serial] = now;

        try
        {
            // Timezone first so the wall clock lands in the right offset; the
            // host TZ is stable, but re-sending it is a cheap no-op.
            var iana = ResolveHostIanaTimeZone();
            if (iana is not null)
            {
                await _client.ExecuteShellCommandAsync(
                    device, $"cmd alarm set-timezone {iana}", new ConsoleOutputReceiver(), ct);
            }
            await _client.ExecuteShellCommandAsync(
                device, $"cmd alarm set-time {now.ToUnixTimeMilliseconds()}", new ConsoleOutputReceiver(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Forget the sync time so the next tick retries.
            _lastClockSyncBySerial.Remove(device.Serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: clock sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Host system timezone as an IANA id (<c>cmd alarm set-timezone</c> wants
    /// IANA, e.g. "America/Los_Angeles"); null when a Windows id has no IANA
    /// mapping, in which case the timezone is left as-is and only the clock set.
    /// </summary>
    private static string? ResolveHostIanaTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId) return local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) ? iana : null;
    }

    /// <summary>
    /// Last-resort backstop for a hard USB-FFS wedge: a host *service* restart can
    /// leave the reverse tunnel listed but passing no traffic, with no reseat to
    /// trigger <see cref="TryRebootOnReseatAsync"/>. qshell keeps its WebView up and
    /// its SPA retries the socket, but across a hard wedge that socket can never
    /// reconnect, so the panel sits on its clock failsafe. A device reboot is the
    /// only reliable clear of a hard wedge.
    ///
    /// Gated on real liveness so the common restart (tunnel fine, or a reverse
    /// remove+re-add cleared a soft wedge) never reboots: the panel's record
    /// <c>LastSeenAt</c> is bumped whenever its SPA reaches the service - on load
    /// and on the nexus-web socket-reconnect refetch (usePanelLayout). If it has
    /// advanced past this run's first sighting, the panel reconnected on its own
    /// through a healthy tunnel, so leave it alone. Only a panel still stale past
    /// <see cref="EscalationGrace"/> is rebooted. The gate trusts any contact with
    /// the q60 record, so an operator opening the device's settings during the
    /// grace also counts as alive; that risks a missed reboot (panel stays on its
    /// clock failsafe), never a spurious one, so it fails safe.
    ///
    /// One reboot per run (<see cref="_escalationRebootedThisRun"/>); shares
    /// <see cref="_lastQshellRebootBySerial"/> with the reseat path so they can't
    /// double-reboot across the re-enumeration.
    /// </summary>
    private async Task TryEscalateRebootAsync(DeviceData device, CancellationToken ct)
    {
        // No anchor → not seen this run yet; nothing to escalate.
        if (!_firstSeenAtBySerial.TryGetValue(device.Serial, out var firstSeenAt)) return;
        if (_escalationRebootedThisRun.Contains(device.Serial)) return;

        var now = DateTimeOffset.UtcNow;
        var sinceFirstSeen = now - firstSeenAt;
        if (sinceFirstSeen < EscalationGrace) return;

        // Liveness gate: skip the reboot if the panel re-contacted the service since
        // we started watching it this run (its SPA reconnected through a healthy
        // tunnel). A reboot here would bounce a recovered panel through the splash.
        var panelLastSeen = QSeriesPanelLastSeenUtcMs();
        if (panelLastSeen is long seenMs && seenMs >= firstSeenAt.ToUnixTimeMilliseconds())
        {
            return;
        }

        if (_lastQshellRebootBySerial.TryGetValue(device.Serial, out var lastReboot)
            && now - lastReboot < QshellRebootCooldown)
        {
            // A reseat reboot just fired - let it play out.
            return;
        }

        try
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: panel never re-contacted the service {sinceFirstSeen.TotalSeconds:F0}s after first sighting; rebooting once to clear a hard USB-FFS wedge");
            await _client.RebootAsync(device, ct);
            // Record the reboot only after it's issued; if RebootAsync throws (stale
            // transport id mid-re-enumeration) leave the flags unset so the next tick
            // retries instead of latching the run as already-rebooted.
            _lastQshellRebootBySerial[device.Serial] = now;
            _escalationRebootedThisRun.Add(device.Serial);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ServiceLog.Info(
                $"[qseries-port-watcher] {device.Serial}: escalation reboot failed: {ex.GetType().Name}: {ex.Message}; will retry next tick");
        }
    }

    /// <summary>
    /// Freshest <c>LastSeenAt</c> (unix ms UTC) among Q-series panel records, or null
    /// if none exist yet. The Q-series posts <c>surface == "q60"</c> for both Q60 and
    /// Q80, and the registry keeps one single-instance record per surface, so this is
    /// the attached panel's last contact with the service.
    /// </summary>
    private long? QSeriesPanelLastSeenUtcMs() =>
        _panelDevices.List()
            .Where(d => string.Equals(d.Capabilities?.Surface, PanelSurfaces.Q60, StringComparison.Ordinal))
            .Select(d => (long?)d.LastSeenAt)
            .FirstOrDefault();

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
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: force-refreshed reverse on first sighting this run (removed {_remoteSpec})");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // No prior reverse (fresh boot) throws here - harmless; the create installs it.
                ServiceLog.Info(
                    $"[qseries-port-watcher] {device.Serial}: reverse pre-refresh remove no-op: {ex.GetType().Name}");
            }
        }

        // allowRebind: true is idempotent - re-binds an existing reverse instead of
        // failing "cannot rebind existing socket" on later ticks. The mapping is always
        // tcp:{port} -> tcp:{port}, so a rebind is a no-op for other consumers.
        try
        {
            await _client.CreateReverseForwardAsync(device, _localSpec, _remoteSpec, true, ct);
            if (!_reverseAppliedBySerial.TryGetValue(device.Serial, out _))
            {
                ServiceLog.Info(
                    $"[qseries-port-watcher] reverse applied: {device.Serial} ({device.Model}) {_localSpec} -> {_remoteSpec}");
                _reverseAppliedBySerial[device.Serial] = true;
            }
        }
        catch (Exception ex)
        {
            // Re-apply runs next tick; forget the applied flag so the recovery re-logs.
            _reverseAppliedBySerial.Remove(device.Serial);
            ServiceLog.Info(
                $"[qseries-port-watcher] reverse apply failed for {device.Serial}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
