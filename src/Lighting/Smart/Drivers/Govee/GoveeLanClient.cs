using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart.Drivers.Govee;

/// <summary>
/// UDP transport for the Govee LAN API. One socket per process bound to the
/// fixed listen port — devices send every reply (scan and devStatus) to the
/// client's IP at that port regardless of the request's source port, so all
/// sends go out of the same socket and one receive loop dispatches replies:
/// scan replies to the active scan collectors, devStatus replies to the
/// per-host waiter. Control commands are fire-and-forget (the protocol has no
/// ACKs). Ports are constructor-injectable for loopback tests.
/// </summary>
public sealed class GoveeLanClient : IDisposable
{
    public const int DefaultScanPort = 4001;
    public const int DefaultListenPort = 4002;
    public const int DefaultControlPort = 4003;
    private static readonly IPAddress DefaultMulticast = IPAddress.Parse("239.255.255.250");

    private readonly IPEndPoint _scanEndpoint;
    private readonly int _scanPort;
    private readonly int _listenPort;
    private readonly int _controlPort;

    private readonly object _lock = new();
    private readonly List<ConcurrentDictionary<string, GoveeDeviceInfo>> _scanSinks = new();
    // Waiters are lists so concurrent requests to the same host (ping racing
    // an identify's status read) all complete from one reply instead of the
    // later registrant displacing the earlier one. Guarded by _lock.
    private readonly Dictionary<string, List<TaskCompletionSource<GoveeReplyData>>> _statusWaiters = new();
    private readonly Dictionary<string, List<TaskCompletionSource<GoveeDeviceInfo>>> _probeWaiters = new();
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public GoveeLanClient(IPEndPoint? scanEndpoint = null, int listenPort = DefaultListenPort, int controlPort = DefaultControlPort)
    {
        _scanEndpoint = scanEndpoint ?? new IPEndPoint(DefaultMulticast, DefaultScanPort);
        _scanPort = _scanEndpoint.Port;
        _listenPort = listenPort;
        _controlPort = controlPort;
    }

    /// <summary>The control port commands are sent to (test hook).</summary>
    public int ControlPort => _controlPort;

    private Socket EnsureSocket()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null) return _socket;

            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // The listen port is fixed by the protocol. ReuseAddress is
            // best-effort coexistence (platform-dependent for UDP unicast); if
            // another LAN controller app (OpenRGB/SignalRGB) owns the port,
            // fall back to an ephemeral one: control keeps working, but scan
            // and status replies are lost — log loudly so "no devices found"
            // is diagnosable.
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                sock.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
            }
            catch (SocketException ex)
            {
                ServiceLog.Warn($"[govee-lan] cannot bind UDP :{_listenPort} ({ex.SocketErrorCode}) — another app owns it; discovery/status replies unavailable");
                sock.Bind(new IPEndPoint(IPAddress.Any, 0));
            }
            try { sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true); } catch { }
            if (_scanEndpoint.Address.Equals(DefaultMulticast))
            {
                // Best-effort group join + no loopback; scanning only needs to
                // SEND to the group, so a failed join still discovers.
                try
                {
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(DefaultMulticast, IPAddress.Any));
                    sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, false);
                }
                catch { }
            }

            _cts = new CancellationTokenSource();
            _socket = sock;
            _ = Task.Run(() => ReceiveLoopAsync(sock, _cts.Token));
            return sock;
        }
    }

    private async Task ReceiveLoopAsync(Socket sock, CancellationToken ct)
    {
        var buf = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult res;
            try
            {
                res = await sock.ReceiveFromAsync(buf, SocketFlags.None,
                    new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                // A persistent error state (adapter removal) must not hot-spin.
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (res.ReceivedBytes <= 0 || res.RemoteEndPoint is not IPEndPoint from) continue;
            try { Dispatch(buf.AsSpan(0, res.ReceivedBytes), from); }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[govee-lan] bad reply from {from.Address}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> payload, IPEndPoint from)
    {
        var env = JsonSerializer.Deserialize(payload, GoveeJsonContext.Default.GoveeEnvelopeGoveeReplyData);
        var cmd = env?.Msg?.Cmd;
        var data = env?.Msg?.Data;
        if (data is null) return;

        if (cmd == "scan" && data.Device is { Length: > 0 } device)
        {
            var info = new GoveeDeviceInfo(
                data.Ip is { Length: > 0 } ip ? ip : from.Address.ToString(),
                device,
                data.Sku ?? "");
            List<TaskCompletionSource<GoveeDeviceInfo>>? probes = null;
            lock (_lock)
            {
                foreach (var sink in _scanSinks) sink[device] = info;
                if (_probeWaiters.Remove(info.Ip, out var list)) probes = list;
            }
            if (probes is not null)
                foreach (var p in probes) p.TrySetResult(info);
        }
        else if (cmd == "devStatus")
        {
            List<TaskCompletionSource<GoveeReplyData>>? waiters = null;
            lock (_lock)
            {
                if (_statusWaiters.Remove(from.Address.ToString(), out var list)) waiters = list;
            }
            if (waiters is not null)
                foreach (var w in waiters) w.TrySetResult(data);
        }
    }

    /// <summary>Multicast/broadcast scan: collect every device that answers
    /// within the window.</summary>
    public async Task<IReadOnlyList<GoveeDeviceInfo>> ScanAsync(int timeoutMs, CancellationToken ct)
    {
        var sink = new ConcurrentDictionary<string, GoveeDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        lock (_lock) _scanSinks.Add(sink);
        try
        {
            await SendScanRequestAsync(_scanEndpoint, ct).ConfigureAwait(false);
            // WiFi APs drop multicast unreliably, but Govee devices also answer a
            // broadcast scan; hit each subnet's directed broadcast so a device the
            // multicast misses still replies.
            foreach (var bcast in BroadcastTargets())
            {
                try { await SendScanRequestAsync(new IPEndPoint(bcast, _scanPort), ct).ConfigureAwait(false); }
                catch (SocketException) { /* a downed interface must not abort the scan */ }
            }
            await Task.Delay(timeoutMs, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _scanSinks.Remove(sink);
        }
        return new List<GoveeDeviceInfo>(sink.Values);
    }

    // Directed broadcast (ip | ~mask) of each up IPv4 interface, so a scan reaches
    // same-segment devices even where multicast is filtered.
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask?.GetAddressBytes();
                if (mask is null || mask.Length != 4) continue;
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }

    /// <summary>Unicast scan probe of one host — confirms LAN Control is
    /// enabled and yields the SKU. Works where multicast is filtered.</summary>
    public async Task<GoveeDeviceInfo?> ProbeAsync(string host, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var addr)) return null;
        var tcs = new TaskCompletionSource<GoveeDeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        AddWaiter(_probeWaiters, host, tcs);
        try
        {
            await SendScanRequestAsync(new IPEndPoint(addr, _scanPort), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            RemoveWaiter(_probeWaiters, host, tcs);
        }
    }

    private void AddWaiter<T>(Dictionary<string, List<TaskCompletionSource<T>>> waiters, string host, TaskCompletionSource<T> tcs)
    {
        lock (_lock)
        {
            if (!waiters.TryGetValue(host, out var list)) { list = new List<TaskCompletionSource<T>>(); waiters[host] = list; }
            list.Add(tcs);
        }
    }

    private void RemoveWaiter<T>(Dictionary<string, List<TaskCompletionSource<T>>> waiters, string host, TaskCompletionSource<T> tcs)
    {
        lock (_lock)
        {
            if (waiters.TryGetValue(host, out var list))
            {
                list.Remove(tcs);
                if (list.Count == 0) waiters.Remove(host);
            }
        }
    }

    private Task SendScanRequestAsync(IPEndPoint target, CancellationToken ct)
    {
        var env = new GoveeEnvelope<GoveeScanRequestData>
        {
            Msg = new GoveeMsg<GoveeScanRequestData> { Cmd = "scan", Data = new GoveeScanRequestData() },
        };
        var json = JsonSerializer.Serialize(env, GoveeJsonContext.Default.GoveeEnvelopeGoveeScanRequestData);
        return SendAsync(target, json, ct);
    }

    /// <summary>Request the device's state; null when it doesn't answer in
    /// time (offline, or LAN Control disabled).</summary>
    public async Task<GoveeReplyData?> StatusAsync(string host, int timeoutMs, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var addr)) return null;
        var tcs = new TaskCompletionSource<GoveeReplyData>(TaskCreationOptions.RunContinuationsAsynchronously);
        AddWaiter(_statusWaiters, host, tcs);
        try
        {
            var env = new GoveeEnvelope<GoveeEmptyData>
            {
                Msg = new GoveeMsg<GoveeEmptyData> { Cmd = "devStatus", Data = new GoveeEmptyData() },
            };
            var json = JsonSerializer.Serialize(env, GoveeJsonContext.Default.GoveeEnvelopeGoveeEmptyData);
            await SendAsync(new IPEndPoint(addr, _controlPort), json, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            RemoveWaiter(_statusWaiters, host, tcs);
        }
    }

    public Task TurnAsync(string host, bool on, CancellationToken ct)
        => SendCommandAsync(host, new GoveeEnvelope<GoveeValueData>
        {
            Msg = new GoveeMsg<GoveeValueData> { Cmd = "turn", Data = new GoveeValueData { Value = on ? 1 : 0 } },
        }, GoveeJsonContext.Default.GoveeEnvelopeGoveeValueData, ct);

    public Task BrightnessAsync(string host, int value, CancellationToken ct)
        => SendCommandAsync(host, new GoveeEnvelope<GoveeValueData>
        {
            Msg = new GoveeMsg<GoveeValueData> { Cmd = "brightness", Data = new GoveeValueData { Value = Math.Clamp(value, 1, 100) } },
        }, GoveeJsonContext.Default.GoveeEnvelopeGoveeValueData, ct);

    /// <summary>Set RGB (kelvin 0) or a white point (kelvin 2000-9000).</summary>
    public Task ColorAsync(string host, byte r, byte g, byte b, int kelvin, CancellationToken ct)
        => SendCommandAsync(host, new GoveeEnvelope<GoveeColorWcData>
        {
            Msg = new GoveeMsg<GoveeColorWcData>
            {
                Cmd = "colorwc",
                Data = new GoveeColorWcData { Color = new GoveeColor { R = r, G = g, B = b }, ColorTemInKelvin = kelvin },
            },
        }, GoveeJsonContext.Default.GoveeEnvelopeGoveeColorWcData, ct);

    /// <summary>Send a base64 razer/DreamView packet (see <see cref="GoveePackets"/>).</summary>
    public Task RazerAsync(string host, string ptBase64, CancellationToken ct)
        => SendCommandAsync(host, new GoveeEnvelope<GoveePtData>
        {
            Msg = new GoveeMsg<GoveePtData> { Cmd = "razer", Data = new GoveePtData { Pt = ptBase64 } },
        }, GoveeJsonContext.Default.GoveeEnvelopeGoveePtData, ct);

    private Task SendCommandAsync<T>(string host, GoveeEnvelope<T> env, System.Text.Json.Serialization.Metadata.JsonTypeInfo<GoveeEnvelope<T>> typeInfo, CancellationToken ct)
    {
        if (!IPAddress.TryParse(host, out var addr)) return Task.CompletedTask;
        var json = JsonSerializer.Serialize(env, typeInfo);
        return SendAsync(new IPEndPoint(addr, _controlPort), json, ct);
    }

    private async Task SendAsync(IPEndPoint target, string json, CancellationToken ct)
    {
        var sock = EnsureSocket();
        var bytes = Encoding.UTF8.GetBytes(json);
        await sock.SendToAsync(bytes, SocketFlags.None, target, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _socket?.Dispose();
            _socket = null;
        }
    }
}
