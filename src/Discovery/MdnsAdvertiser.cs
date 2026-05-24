using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Discovery;

/// <summary>
/// Minimal DNS-SD over mDNS responder for the iOS companion app's
/// Bonjour discovery. Advertises the local TLS pair endpoint as
/// <c>&lt;instance&gt;._qos._tcp.local.</c> on port 9443 with TXT records
/// <c>name=&lt;MachineName&gt;</c>, <c>fp=&lt;SPKI base64url, no pad&gt;</c>,
/// <c>v=&lt;service version&gt;</c>.
///
/// Hand-rolled rather than pulling Makaretu.Dns / Tmds.MDns so the macOS
/// AOT publish stays trim-clean (no reflection, no managed deps).
/// </summary>
public sealed class MdnsAdvertiser : IHostedService, IDisposable
{
    private const string ServiceType = "_qos._tcp.local.";
    private const int MdnsPort = 5353;
    private static readonly IPAddress MdnsV4 = IPAddress.Parse("224.0.0.251");

    private readonly ILogger<MdnsAdvertiser> _log;
    private readonly PanelPhonePairingService _pairing;
    private readonly IConfigStore _store;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private readonly List<Socket> _sockets = new();
    private Task? _runLoop;
    private Timer? _expiryTimer;
    private byte[]? _cachedAnnouncement;
    private string _hostLabel = "nexus";
    private string _instanceLabel = "Nexus";
    private int _httpsPort;
    private string _machineName = "";
    private string _spki = "";
    private bool _broadcasting;

    public MdnsAdvertiser(ILogger<MdnsAdvertiser> log, PanelPhonePairingService pairing, IConfigStore store)
    {
        _log = log;
        _pairing = pairing;
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _httpsPort = _pairing.HttpsPort;
        _machineName = _pairing.MachineName;
        _spki = _pairing.SpkiFingerprint ?? string.Empty;

        if (_httpsPort <= 0)
        {
            _log.LogInformation("mDNS advertiser disabled: no HTTPS pair port available.");
            return Task.CompletedTask;
        }

        _hostLabel = SanitiseDnsLabel(_machineName, fallback: "nexus");
        _instanceLabel = string.IsNullOrWhiteSpace(_machineName) ? "Nexus" : _machineName;

        _store.OnChanged += OnSettingsChanged;
        ApplyBroadcastPreference();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _store.OnChanged -= OnSettingsChanged;
        await StopBroadcastingAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _runCts?.Cancel(); } catch { /* ignored */ }
        _runCts?.Dispose();
        _expiryTimer?.Dispose();
    }

    private void OnSettingsChanged()
    {
        // IConfigStore.OnChanged fires on the persistence thread; hop off
        // before touching socket state so we never block writers.
        _ = Task.Run(ApplyBroadcastPreference);
    }

    private void ApplyBroadcastPreference()
    {
        var (mode, until) = _pairing.GetPairBroadcast();
        var shouldBroadcast = mode == "always" || mode == "until";

        lock (_gate)
        {
            if (shouldBroadcast && !_broadcasting)
            {
                StartBroadcastingLocked();
            }
            else if (!shouldBroadcast && _broadcasting)
            {
                _ = StopBroadcastingAsync();
            }

            // Schedule auto-stop when the "until" window expires. Drop any
            // prior timer first - users can move from one preference to
            // another freely, and a leftover timer would race.
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            if (mode == "until" && until > 0)
            {
                var msUntil = Math.Max(0, until - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) * 1000L;
                _expiryTimer = new Timer(_ => OnExpiry(), null, msUntil, Timeout.Infinite);
            }
        }
    }

    private void OnExpiry()
    {
        // Window elapsed. Persist as "never" (read-side already collapses
        // expired entries to never, but writing makes the dashboard UI
        // reflect the change without waiting for a refetch) then re-apply.
        _pairing.SetPairBroadcast("never", 0);
    }

    private void StartBroadcastingLocked()
    {
        try
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            OpenSockets();
            _cachedAnnouncement = BuildAnnouncement(IPv4Addresses());
            _runLoop = Task.Run(() => RunAsync(_runCts.Token));
            _broadcasting = true;
            _log.LogInformation("mDNS advertiser online: {Instance}._qos._tcp.local on port {Port}", _instanceLabel, _httpsPort);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "mDNS advertiser failed to start; Wi-Fi discovery from the iOS app will not work on this host.");
            CleanupSockets();
            _broadcasting = false;
        }
    }

    private async Task StopBroadcastingAsync()
    {
        Task? runLoop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_broadcasting) return;
            _broadcasting = false;
            cts = _runCts;
            _runCts = null;
            runLoop = _runLoop;
            _runLoop = null;
        }
        try { cts?.Cancel(); } catch { /* ignored */ }
        try
        {
            await SendGoodbyeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS goodbye failed; clients will time out the record via TTL.");
        }
        if (runLoop is not null)
        {
            try { await runLoop.ConfigureAwait(false); } catch { /* ignored */ }
        }
        CleanupSockets();
        cts?.Dispose();
        _log.LogInformation("mDNS advertiser stopped (broadcast preference off).");
    }

    private void CleanupSockets()
    {
        foreach (var s in _sockets)
        {
            try { s.Close(); } catch { /* ignored */ }
            s.Dispose();
        }
        _sockets.Clear();
    }

    private void OpenSockets()
    {
        // Bind one socket per up-and-running IPv4 interface so the responder
        // hears + replies on every LAN the host is on. SO_REUSEADDR (and
        // SO_REUSEPORT on Unix) lets us co-exist with the system mDNS
        // responder on macOS. If binding 5353 fails on a given interface,
        // log and skip - announcements still go out on the others.
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!nic.SupportsMulticast) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip)) continue;
                if (ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;

                Socket? s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    TrySetReusePort(s);
                    s.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsV4, ip));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    // IP_MULTICAST_LOOP is per-socket but it gates whether
                    // multicast packets sent from THIS socket are delivered
                    // back into the local-host loopback path. Keep it on:
                    // the system mDNSResponder and the iOS Simulator both
                    // share the host's stack and need to see our packets;
                    // the receive loop discards responses (QR=1) and only
                    // acts on real queries (QR=0), so self-echo is harmless.
                    s.MulticastLoopback = true;
                    _sockets.Add(s);
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "mDNS: skipped interface {Nic} addr {Ip} ({Reason})", nic.Name, ip, ex.Message);
                    try { s?.Close(); } catch { /* ignored */ }
                    s?.Dispose();
                }
            }
        }

        if (_sockets.Count == 0)
        {
            throw new InvalidOperationException("no usable IPv4 multicast interfaces");
        }
    }

    private static void TrySetReusePort(Socket s)
    {
        // SO_REUSEPORT is Unix-only and has no portable enum in .NET; raw value is 15 on macOS/Linux.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try { s.SetRawSocketOption(/*SOL_SOCKET*/ 0xffff, /*SO_REUSEPORT*/ 0x0200, BitConverter.GetBytes(1)); }
            catch
            {
                try { s.SetRawSocketOption(/*SOL_SOCKET*/ 1, /*SO_REUSEPORT Linux*/ 15, BitConverter.GetBytes(1)); }
                catch { /* best effort */ }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Phase 1: rapid unsolicited announcements (4x at 1s interval), then
        // settle into a 60s refresh cadence. Spec-style. Each socket also
        // listens for incoming queries and answers PTR/SRV/TXT/A.
        _ = Task.Run(() => AnnounceLoopAsync(ct));

        var listenTasks = new List<Task>();
        foreach (var s in _sockets)
        {
            listenTasks.Add(Task.Run(() => ListenAsync(s, ct)));
        }
        await Task.WhenAll(listenTasks).ConfigureAwait(false);
    }

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < 4 && !ct.IsCancellationRequested; i++)
            {
                await BroadcastAnnouncementAsync(ct).ConfigureAwait(false);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                await BroadcastAnnouncementAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "mDNS announce loop ended");
        }
    }

    private async Task BroadcastAnnouncementAsync(CancellationToken ct)
    {
        // Rebuild every announcement: a Wi-Fi switch, sleep/resume, or DHCP
        // renew changes the host's IPv4 address, and a cached packet would
        // keep advertising the stale address until the next service restart.
        var packet = BuildAnnouncement(IPv4Addresses());
        _cachedAnnouncement = packet;
        var dest = new IPEndPoint(MdnsV4, MdnsPort);
        foreach (var s in _sockets)
        {
            try { await s.SendToAsync(packet, SocketFlags.None, dest, ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogDebug(ex, "mDNS announce send failed"); }
        }
    }

    private async Task SendGoodbyeAsync()
    {
        var packet = BuildAnnouncement(IPv4Addresses(), ttl: 0);
        var dest = new IPEndPoint(MdnsV4, MdnsPort);
        foreach (var s in _sockets)
        {
            try { await s.SendToAsync(packet, SocketFlags.None, dest).ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    private async Task ListenAsync(Socket socket, CancellationToken ct)
    {
        var buf = new byte[2048];
        var remote = new IPEndPoint(IPAddress.Any, 0) as EndPoint;
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                var r = await socket.ReceiveFromAsync(buf, SocketFlags.None, remote, ct).ConfigureAwait(false);
                n = r.ReceivedBytes;
                remote = r.RemoteEndPoint;
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "mDNS recv failed");
                continue;
            }

            try
            {
                if (TryHandleQuery(buf.AsSpan(0, n), out var reply) && reply is not null)
                {
                    var dest = new IPEndPoint(MdnsV4, MdnsPort);
                    await socket.SendToAsync(reply, SocketFlags.None, dest, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "mDNS reply failed");
            }
        }
    }

    // ── DNS wire-format helpers ──────────────────────────────────────────

    private List<IPAddress> IPv4Addresses()
    {
        var addrs = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip)) continue;
                if (ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                addrs.Add(ip);
            }
        }
        return addrs;
    }

    private bool TryHandleQuery(ReadOnlySpan<byte> packet, out byte[]? reply)
    {
        reply = null;
        if (packet.Length < 12) return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
        if ((flags & 0x8000) != 0) return false; // response, not a query
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        if (qdCount == 0) return false;

        var pos = 12;
        var wantsService = false;
        var wantsInstance = false;
        var wantsHost = false;

        for (var i = 0; i < qdCount; i++)
        {
            if (!TryReadName(packet, ref pos, out var name)) return false;
            if (packet.Length < pos + 4) return false;
            var qType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(pos, 2));
            pos += 4; // qtype + qclass

            if (name.Equals(ServiceType, StringComparison.OrdinalIgnoreCase) && (qType == 12 /*PTR*/ || qType == 255 /*ANY*/))
                wantsService = true;
            if (name.Equals(InstanceName, StringComparison.OrdinalIgnoreCase) && (qType == 33 /*SRV*/ || qType == 16 /*TXT*/ || qType == 255))
                wantsInstance = true;
            if (name.Equals(_hostLabel + ".local.", StringComparison.OrdinalIgnoreCase) && (qType == 1 /*A*/ || qType == 255))
                wantsHost = true;
        }

        if (!wantsService && !wantsInstance && !wantsHost) return false;
        reply = BuildAnnouncement(IPv4Addresses(),
            includeServicePtr: wantsService,
            includeSrvTxt: wantsService || wantsInstance,
            includeA: wantsService || wantsInstance || wantsHost);
        return true;
    }

    private string InstanceName => _instanceLabel + "." + ServiceType;

    private byte[] BuildAnnouncement(
        List<IPAddress> addresses,
        bool includeServicePtr = true,
        bool includeSrvTxt = true,
        bool includeA = true,
        uint ttl = 120)
    {
        // Build a response packet with our PTR / SRV / TXT / A records.
        // Single-pass writer; never compresses names (slight wire-size cost
        // but trivial to read and AOT-friendly).
        using var ms = new System.IO.MemoryStream();

        // Header
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(0, 2), 0); // ID 0 for mDNS responses
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(2, 2), 0x8400); // QR=1, AA=1
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(4, 2), 0); // QDCOUNT
        ushort answers = 0;
        if (includeServicePtr) answers += 1;
        if (includeSrvTxt) answers += 2;
        if (includeA) answers += (ushort)addresses.Count;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(6, 2), answers);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(8, 2), 0); // NSCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(10, 2), 0); // ARCOUNT
        ms.Write(hdr);

        var instance = InstanceName;
        var host = _hostLabel + ".local.";

        if (includeServicePtr)
        {
            WriteRecord(ms, ServiceType, type: 12, classAndFlush: 0x0001 /*IN, no flush*/, ttl, EncodeName(instance));
        }
        if (includeSrvTxt)
        {
            // SRV: priority(2) weight(2) port(2) target(name)
            var target = EncodeName(host);
            var srv = new byte[6 + target.Length];
            BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(0, 2), 0); // priority
            BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(2, 2), 0); // weight
            BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(4, 2), (ushort)_httpsPort);
            target.CopyTo(srv, 6);
            WriteRecord(ms, instance, type: 33, classAndFlush: 0x8001 /*IN + cache-flush*/, ttl, srv);

            // TXT records
            WriteRecord(ms, instance, type: 16, classAndFlush: 0x8001, ttl, EncodeTxt());
        }
        if (includeA)
        {
            foreach (var ip in addresses)
            {
                WriteRecord(ms, host, type: 1, classAndFlush: 0x8001, ttl, ip.GetAddressBytes());
            }
        }
        return ms.ToArray();
    }

    private static void WriteRecord(System.IO.MemoryStream ms, string name, ushort type, ushort classAndFlush, uint ttl, byte[] rdata)
    {
        var encoded = EncodeName(name);
        ms.Write(encoded);
        Span<byte> hdr = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(0, 2), type);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(2, 2), classAndFlush);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(4, 4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(8, 2), (ushort)rdata.Length);
        ms.Write(hdr);
        ms.Write(rdata);
    }

    private static byte[] EncodeName(string name)
    {
        // Encode "Foo.bar.local." → length-prefixed labels terminated by 0x00.
        var parts = name.TrimEnd('.').Split('.');
        var size = 1;
        foreach (var p in parts) size += 1 + Encoding.UTF8.GetByteCount(p);
        var buf = new byte[size];
        var i = 0;
        foreach (var p in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(p);
            if (bytes.Length > 63) throw new InvalidOperationException("DNS label > 63 bytes");
            buf[i++] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, buf, i, bytes.Length);
            i += bytes.Length;
        }
        buf[i] = 0;
        return buf;
    }

    private byte[] EncodeTxt()
    {
        // TXT rdata = sequence of length-prefixed strings. Empty TXT is one
        // single 0x00 byte per RFC 1035 §3.3.14.
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(_machineName)) items.Add("name=" + _machineName);
        if (!string.IsNullOrWhiteSpace(_spki)) items.Add("fp=" + _spki);
        if (!string.IsNullOrWhiteSpace(BuildInfo.Version)) items.Add("v=" + BuildInfo.Version);
        if (items.Count == 0) return new byte[] { 0 };
        var size = 0;
        foreach (var item in items) size += 1 + Encoding.UTF8.GetByteCount(item);
        var buf = new byte[size];
        var i = 0;
        foreach (var item in items)
        {
            var bytes = Encoding.UTF8.GetBytes(item);
            if (bytes.Length > 255) bytes = bytes.AsSpan(0, 255).ToArray();
            buf[i++] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, buf, i, bytes.Length);
            i += bytes.Length;
        }
        return buf;
    }

    private static bool TryReadName(ReadOnlySpan<byte> packet, ref int pos, out string name)
    {
        // mDNS clients sometimes use compression pointers; follow them once.
        var sb = new StringBuilder();
        var p = pos;
        var jumped = false;
        var origPos = pos;
        var hops = 0;
        while (true)
        {
            if (p >= packet.Length) { name = ""; return false; }
            var len = packet[p];
            if (len == 0)
            {
                p++;
                if (!jumped) pos = p;
                name = sb.ToString();
                return true;
            }
            if ((len & 0xC0) == 0xC0)
            {
                if (p + 1 >= packet.Length) { name = ""; return false; }
                var offset = ((len & 0x3F) << 8) | packet[p + 1];
                if (!jumped) { pos = p + 2; jumped = true; }
                p = offset;
                if (++hops > 8) { name = ""; return false; }
                continue;
            }
            if (p + 1 + len > packet.Length) { name = ""; return false; }
            sb.Append(Encoding.UTF8.GetString(packet.Slice(p + 1, len)));
            sb.Append('.');
            p += 1 + len;
        }
    }

    private static string SanitiseDnsLabel(string? raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var sb = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
            else sb.Append('-');
        }
        var label = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(label) ? fallback : label;
    }
}

