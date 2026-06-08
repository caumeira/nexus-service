using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart.Drivers.Hue;

/// <summary>
/// One live Hue Entertainment streaming session to a bridge: starts an
/// entertainment configuration, opens the DTLS-PSK channel, and streams
/// HueStream v2 packets (all channels in one UDP datagram, ~25–60 Hz). This is
/// the low-latency path that REST per-light updates can't match for many lights.
///
/// v1 uses an EXISTING entertainment configuration (an "Entertainment Area"
/// created in the Hue app). Lights→channels are resolved via each light's
/// owning device → its entertainment service → the config channel.
/// </summary>
public sealed class HueEntertainmentSession : IDisposable
{
    private const int StreamPort = 2100;

    private readonly HueBridgeClient _client;
    private readonly string _host;
    private readonly string _appKey;
    private readonly string _clientKeyHex;

    private string _configId = "";
    private readonly Dictionary<string, int> _lightToChannel = new(StringComparer.Ordinal);
    private DtlsPskClient? _dtls;
    private byte _seq;

    public HueEntertainmentSession(HueBridgeClient client, string host, string appKey, string clientKeyHex)
    {
        _client = client;
        _host = host;
        _appKey = appKey;
        _clientKeyHex = clientKeyHex;
    }

    public bool Active => _dtls is not null;
    public bool Maps(string lightRid) => _lightToChannel.ContainsKey(lightRid);

    /// <summary>Pick a config, map lights→channels, start streaming, DTLS-connect.
    /// Throws on no entertainment area, missing clientkey, or handshake failure.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_clientKeyHex))
            throw new InvalidOperationException("no clientkey — re-pair the bridge to enable Entertainment");

        var configs = await _client.GetEntertainmentConfigsAsync(_host, _appKey, ct).ConfigureAwait(false);
        HueEntConfig? chosen = null;
        foreach (var c in configs) { if (c.Channels.Count > 0) { chosen = c; break; } }
        if (chosen is null)
            throw new InvalidOperationException("no Hue Entertainment Area configured — create one in the Hue app");
        _configId = chosen.Id;

        await BuildChannelMapAsync(chosen, ct).ConfigureAwait(false);

        await _client.SetEntertainmentActionAsync(_host, _appKey, _configId, "start", ct).ConfigureAwait(false);

        var psk = Convert.FromHexString(_clientKeyHex);
        _dtls = await DtlsPskClient.ConnectAsync(_host, StreamPort, _appKey, psk, ct).ConfigureAwait(false);
        ServiceLog.Info($"[hue-entertainment] streaming '{chosen.Metadata?.Name}' ({_lightToChannel.Count} channels mapped)");
    }

    // lightRid → owning device → entertainment service → config channel id.
    private async Task BuildChannelMapAsync(HueEntConfig config, CancellationToken ct)
    {
        var lights = await _client.GetLightsAsync(_host, _appKey, ct).ConfigureAwait(false);
        var services = await _client.GetEntertainmentServicesAsync(_host, _appKey, ct).ConfigureAwait(false);

        var lightToDevice = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lights)
            if (l.Owner?.Rid is { Length: > 0 } d) lightToDevice[l.Id] = d;

        var deviceToEntService = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in services)
            if (s.Owner?.Rid is { Length: > 0 } d) deviceToEntService[d] = s.Id;

        var entServiceToChannel = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ch in config.Channels)
            foreach (var m in ch.Members)
                if (m.Service?.Rid is { Length: > 0 } rid) entServiceToChannel[rid] = ch.ChannelId;

        _lightToChannel.Clear();
        foreach (var (lightRid, device) in lightToDevice)
            if (deviceToEntService.TryGetValue(device, out var ent) && entServiceToChannel.TryGetValue(ent, out var chId))
                _lightToChannel[lightRid] = chId;
    }

    /// <summary>Stream one frame: a color per mapped light. Channels with no
    /// supplied color are omitted (the bridge holds their last value).</summary>
    public void Push(IReadOnlyDictionary<string, (byte r, byte g, byte b)> colorsByLightRid)
    {
        var dtls = _dtls;
        if (dtls is null || _lightToChannel.Count == 0) return;

        // Collect (channel, rgb) for the lights we have colors for.
        Span<(byte ch, byte r, byte g, byte b)> chans = stackalloc (byte, byte, byte, byte)[_lightToChannel.Count];
        var n = 0;
        foreach (var (rid, color) in colorsByLightRid)
            if (_lightToChannel.TryGetValue(rid, out var chId))
                chans[n++] = ((byte)chId, color.r, color.g, color.b);
        if (n == 0) return;

        var packet = BuildPacket(_configId, chans.Slice(0, n), unchecked(_seq++));
        try { dtls.Send(packet); } catch { /* UDP best-effort; next frame retries */ }
    }

    // HueStream v2: "HueStream"(9) + ver(02 00) + seq(1) + reserved(00 00) +
    // colorspace(00=RGB) + reserved(00) + configId ASCII(36) + per channel:
    // id(1) + R(2 BE) + G(2 BE) + B(2 BE). 8-bit → 16-bit via v<<8|v.
    internal static byte[] BuildPacket(string configId, ReadOnlySpan<(byte ch, byte r, byte g, byte b)> chans, byte seq)
    {
        var idBytes = Encoding.ASCII.GetBytes(configId);
        var packet = new byte[16 + idBytes.Length + chans.Length * 7];
        var o = 0;
        Encoding.ASCII.GetBytes("HueStream").CopyTo(packet, o); o += 9;
        packet[o++] = 0x02; packet[o++] = 0x00; // version 2.0
        packet[o++] = seq;                       // sequence id
        packet[o++] = 0x00; packet[o++] = 0x00;  // reserved
        packet[o++] = 0x00;                       // color space: RGB
        packet[o++] = 0x00;                       // reserved
        idBytes.CopyTo(packet, o); o += idBytes.Length;
        foreach (var c in chans)
        {
            packet[o++] = c.ch;
            packet[o++] = c.r; packet[o++] = c.r;
            packet[o++] = c.g; packet[o++] = c.g;
            packet[o++] = c.b; packet[o++] = c.b;
        }
        return packet;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        var dtls = _dtls;
        _dtls = null;
        try { dtls?.Dispose(); } catch { }
        if (!string.IsNullOrEmpty(_configId))
        {
            try { await _client.SetEntertainmentActionAsync(_host, _appKey, _configId, "stop", ct).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
    }

    public void Dispose()
    {
        try { _dtls?.Dispose(); } catch { }
        _dtls = null;
    }
}
