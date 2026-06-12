using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart.Discovery;
using Nexus.Service.Persistence;
using Nexus.Service.Security;

namespace Nexus.Service.Lighting.Smart.Drivers.Nanoleaf;

/// <summary>
/// Nanoleaf driver: panel products (Light Panels, Canvas, Shapes, Elements,
/// Lines) and Matter WiFi Essentials (strips/bulbs) over the local Open API.
/// One paired entry per controller; its panels (or strip LEDs) are the zones
/// of that one device, placed on the canvas via the real panel layout.
/// Control = REST state endpoints; effect streaming = External Control v2
/// (UDP). Pairing needs the device's pairing window open (hold the power
/// button 5-7 s, or "Connect to API" in the Nanoleaf app for Essentials).
/// </summary>
public sealed class NanoleafDriver : ILightDriver, IDisposable
{
    public const int DefaultRestPort = 16021;
    public const int DefaultStreamPort = 60222;
    private const string MdnsService = "_nanoleafapi._tcp";
    // Official extControl ceiling is 10 Hz; Shapes-era firmware drops packets
    // spaced closer than ~50 ms.
    private const int StreamIntervalMs = 100;
    // A controller reboot drops extControl silently (UDP gives no feedback) —
    // re-issue the idempotent enable periodically so streaming self-heals.
    private const int StreamRearmMs = 30_000;
    // Layout parts that never emit light: rhythm module, Shapes controller,
    // Lines connector, controller cap, power connector.
    private static readonly HashSet<int> NonLightShapes = new() { 1, 12, 16, 19, 20 };

    private readonly NanoleafClient _client;
    private readonly LanDiscovery _lan;
    private readonly IConfigStore _store;
    private readonly int _restPort;
    private readonly int _streamPort;
    private readonly Socket _udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    // Parsed Extra payloads, keyed by device id and invalidated when the raw
    // string changes (re-pair) — PlanFrames/SendAsync run per tick.
    private readonly ConcurrentDictionary<string, (string Raw, NanoleafExtra Parsed)> _extras = new();
    // Devices in extControl mode → TickCount64 of the last enable (entered on
    // the first zone frame, re-armed periodically, exited by the first static
    // send — a hue/sat state PUT leaves extControl).
    private readonly ConcurrentDictionary<string, long> _streamArmedAt = new();

    public NanoleafDriver(NanoleafClient client, LanDiscovery lan, IConfigStore store,
        int restPort = DefaultRestPort, int streamPort = DefaultStreamPort)
    {
        _client = client;
        _lan = lan;
        _store = store;
        _restPort = restPort;
        _streamPort = streamPort;
    }

    public string Brand => "nanoleaf";

    public int MinIntervalMs(SmartLight dev) => StreamIntervalMs;

    // One controller per paired device; controllers are independent.
    public string RateLimitKey(SmartLight dev) => "nanoleaf:" + dev.Host;

    public async Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
    {
        var hosts = await _lan.MdnsHostsAsync(MdnsService, 2000, ct).ConfigureAwait(false);

        // The info endpoint is token-gated, so an unpaired controller can't be
        // probed for its serial — map already-paired hosts back to their
        // identity so the UI can mark them as paired.
        var paired = new Dictionary<string, SmartLightConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in _store.Load().SmartLights.Devices)
        {
            if (string.Equals(cfg.Brand, Brand, StringComparison.OrdinalIgnoreCase))
                paired[cfg.Host] = cfg;
        }

        var result = new List<DiscoveredLight>(hosts.Count);
        foreach (var host in hosts)
        {
            paired.TryGetValue(host, out var known);
            result.Add(new DiscoveredLight(Brand, host,
                known?.Name is { Length: > 0 } n ? n : "Nanoleaf",
                known?.StableKey is { Length: > 0 } k ? k : host));
        }
        return result;
    }

    public async Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct)
    {
        // The UDP streaming path needs an IPv4 literal (no DNS per frame) —
        // resolve a user-typed hostname once, here.
        var host = await LanHost.ResolveIpv4Async(target.Host, ct).ConfigureAwait(false);
        if (host is null)
            return new PairResult { Ok = false, Error = "host-not-found" };

        string? token;
        try { token = await _client.CreateTokenAsync(host, _restPort, ct).ConfigureAwait(false); }
        catch (Exception ex) { return new PairResult { Ok = false, Error = ex.Message }; }
        if (token is null)
            return new PairResult { Ok = false, Error = "pairing-mode" };

        var info = await _client.GetInfoAsync(host, _restPort, token, ct).ConfigureAwait(false);
        if (info is null)
        {
            // Don't leave the just-created token registered on the controller.
            try { await _client.DeleteTokenAsync(host, _restPort, token, ct).ConfigureAwait(false); } catch { }
            return new PairResult { Ok = false, Error = "info-failed" };
        }

        var extra = new NanoleafExtra { Port = _restPort, StreamPort = _streamPort };
        var panels = new List<NanoleafPanelPosition>();
        foreach (var p in info.PanelLayout?.Layout?.PositionData ?? new List<NanoleafPanelPosition>())
            if (!NonLightShapes.Contains(p.ShapeType)) panels.Add(p);

        if (panels.Count > 0)
        {
            extra.Kind = NanoleafExtra.KindPanels;
            extra.PanelIds = new int[panels.Count];
            (extra.U, extra.V) = NormalizeLayout(panels);
            for (var i = 0; i < panels.Count; i++) extra.PanelIds[i] = panels[i].PanelId;
            extra.LedCount = panels.Count;
        }
        else
        {
            // Essentials: no layout; zones are LED indices along the strip.
            extra.Kind = NanoleafExtra.KindLeds;
            extra.LedCount = Math.Max(1,
                await _client.GetLedCountAsync(host, _restPort, token, ct).ConfigureAwait(false) ?? 1);
        }

        var serial = info.SerialNo is { Length: > 0 } s ? s
            : target.StableKey is { Length: > 0 } k ? k
            : host;

        return new PairResult
        {
            Ok = true,
            Devices = new List<SmartLightConfig>
            {
                new()
                {
                    Id = $"nanoleaf:{serial}",
                    Brand = Brand,
                    Name = info.Name is { Length: > 0 } n ? n : "Nanoleaf",
                    Host = host,
                    StableKey = serial,
                    Token = SecretProtector.Protect(token),
                    Extra = JsonSerializer.Serialize(extra, NanoleafJsonContext.Default.NanoleafExtra),
                    Enabled = true,
                },
            },
        };
    }

    /// <summary>Normalized panel-centroid UVs. Layout x/y are centroids in the
    /// device's own units (y up); the canvas v axis points down, so v flips.</summary>
    internal static (float[] u, float[] v) NormalizeLayout(List<NanoleafPanelPosition> panels)
    {
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var p in panels)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        float rx = maxX - minX, ry = maxY - minY;
        var u = new float[panels.Count];
        var v = new float[panels.Count];
        for (var i = 0; i < panels.Count; i++)
        {
            u[i] = rx > 0 ? (panels[i].X - minX) / rx : 0.5f;
            v[i] = ry > 0 ? 1f - (panels[i].Y - minY) / ry : 0.5f;
        }
        return (u, v);
    }

    public LightFramePlan PlanFrames(SmartLight dev)
    {
        var extra = GetExtra(dev);
        if (extra is null) return new LightFramePlan(16, AverageToSingle: true);
        if (extra.Kind == NanoleafExtra.KindPanels && extra.PanelIds.Length > 0)
            return new LightFramePlan(extra.PanelIds.Length, AverageToSingle: false, extra.U, extra.V);
        if (extra.LedCount > 1)
            return new LightFramePlan(extra.LedCount, AverageToSingle: false);
        // Single-zone bulb: average a small canvas grid like Hue.
        return new LightFramePlan(16, AverageToSingle: true);
    }

    public async Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct)
    {
        var extra = GetExtra(dev);
        if (extra is null) return;
        var token = SecretProtector.Unprotect(dev.Token);
        if (string.IsNullOrEmpty(token)) return;

        if (frame.Zones is { } zones && frame.On)
        {
            var now = Environment.TickCount64;
            if (!_streamArmedAt.TryGetValue(dev.Id, out var armedAt) || now - armedAt > StreamRearmMs)
            {
                // Streamed colors are still multiplied by the device's
                // brightness state — pin it to 100 and carry brightness in the
                // packed RGB instead.
                await _client.PutStateAsync(dev.Host, extra.Port, token, new NanoleafStateWrite
                {
                    Brightness = new NanoleafIntValue { Value = 100 },
                    On = new NanoleafBoolValue { Value = true },
                }, ct).ConfigureAwait(false);
                await _client.EnableExtControlAsync(dev.Host, extra.Port, token, ct).ConfigureAwait(false);
                _streamArmedAt[dev.Id] = now;
            }
            SendZones(extra, dev.Host, zones, frame.Brightness01);
            return;
        }

        // Static path; a hue/sat state PUT also pulls the device out of
        // extControl after an effect stops.
        _streamArmedAt.TryRemove(dev.Id, out _);
        var write = new NanoleafStateWrite();
        var (h, s, v) = ColorMath.RgbToHsv(frame.R, frame.G, frame.B);
        if (!frame.On || v <= 0f)
        {
            // Black with On=true would clamp to brightness 1 (dimly lit
            // panels) — translate it to off like a lamp would behave.
            write.On = new NanoleafBoolValue { Value = false };
        }
        else
        {
            write.Hue = new NanoleafIntValue { Value = (int)Math.Round(h * 360) % 360 };
            write.Sat = new NanoleafIntValue { Value = Math.Clamp((int)Math.Round(s * 100), 0, 100) };
            write.Brightness = new NanoleafIntValue
            {
                Value = Math.Clamp((int)Math.Round(v * Math.Clamp(frame.Brightness01, 0f, 1f) * 100), 1, 100),
            };
            write.On = new NanoleafBoolValue { Value = true };
        }
        await _client.PutStateAsync(dev.Host, extra.Port, token, write, ct).ConfigureAwait(false);
    }

    private void SendZones(NanoleafExtra extra, string host, byte[] zones, float brightness01)
    {
        if (!IPAddress.TryParse(host, out var addr)) return;
        var ids = extra.Kind == NanoleafExtra.KindPanels ? extra.PanelIds : Array.Empty<int>();
        var zoneCount = Math.Min(zones.Length / 3,
            extra.Kind == NanoleafExtra.KindPanels ? ids.Length : extra.LedCount);
        var ep = new IPEndPoint(addr, extra.StreamPort);
        for (var start = 0; start < zoneCount; start += NanoleafPackets.MaxZonesPerDatagram)
        {
            var count = Math.Min(NanoleafPackets.MaxZonesPerDatagram, zoneCount - start);
            var pkt = NanoleafPackets.BuildV2Datagram(ids, start, count, zones, brightness01, transitionTenths: 1);
            try { _udp.SendTo(pkt, ep); }
            catch (SocketException) { /* fire-and-forget; online status comes from PingAsync */ }
        }
    }

    public async Task IdentifyAsync(SmartLight dev, CancellationToken ct)
    {
        var extra = GetExtra(dev);
        var token = SecretProtector.Unprotect(dev.Token);
        if (extra is null || string.IsNullOrEmpty(token)) return;
        try { await _client.IdentifyAsync(dev.Host, extra.Port, token, ct).ConfigureAwait(false); }
        catch { /* not supported on Essentials — non-fatal */ }
    }

    public async Task<bool> PingAsync(SmartLight dev, CancellationToken ct)
    {
        var extra = GetExtra(dev);
        var token = SecretProtector.Unprotect(dev.Token);
        if (extra is null || string.IsNullOrEmpty(token)) return false;
        try { return await _client.PingAsync(dev.Host, extra.Port, token, ct).ConfigureAwait(false); }
        catch { return false; }
    }

    private NanoleafExtra? GetExtra(SmartLight dev)
    {
        if (string.IsNullOrEmpty(dev.Extra)) return null;
        if (_extras.TryGetValue(dev.Id, out var cached) && cached.Raw == dev.Extra) return cached.Parsed;
        try
        {
            var parsed = JsonSerializer.Deserialize(dev.Extra, NanoleafJsonContext.Default.NanoleafExtra);
            if (parsed is null) return null;
            _extras[dev.Id] = (dev.Extra, parsed);
            return parsed;
        }
        catch { return null; }
    }

    public void Dispose() => _udp.Dispose();
}
