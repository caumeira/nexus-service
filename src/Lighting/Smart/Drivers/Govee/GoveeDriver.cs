using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting.Smart.Drivers.Govee;

/// <summary>
/// Govee driver (Wi-Fi devices with "LAN Control" enabled in the Govee Home
/// app - there is no token pairing). Discovery via multicast scan, control via
/// UDP JSON, realtime per-segment streaming via the binary razer/DreamView
/// command on RGBIC models. The LAN API exposes no capability metadata, so
/// the realtime segment count comes from a per-SKU table captured at pair
/// time and degrades to averaged single-color for unknown bulb-class models.
/// </summary>
public sealed class GoveeDriver : ILightDriver
{
    // DreamView streaming cadence; the strips' own LED refresh is ~20 ms, so
    // pushing faster than ~30 fps buys nothing and risks flooding.
    private const int RazerIntervalMs = 33;
    private const int ControlIntervalMs = 100;
    private const int ProbeTimeoutMs = 2000;
    // Devices drop out of razer mode after ~1 min without frames AND on any
    // power cycle, with no feedback either way, and the enable is fire-and-forget
    // UDP - a single re-arm lost while the strip wakes from a power-off strands it
    // in its native scene. Re-arm every couple seconds so a dropped enable
    // self-heals in ~2s instead of waiting out the revert window.
    private const int RazerRearmMs = 2_000;

    // Bulb / single-zone models on the official LAN list (H60xx lamp class):
    // control-only, averaged to one color.
    private static readonly HashSet<string> SingleColorSkus = new(StringComparer.OrdinalIgnoreCase)
    {
        "H6002", "H6003", "H6004", "H6006", "H6008", "H6009", "H600A", "H600D", "H6010", "H6011", "H601A",
    };

    // Per-IC color counts for razer frames on models where the count is known;
    // everything else uses the default below (matches OpenRGB's behavior for
    // unknown SKUs). Refine as hardware feedback comes in.
    private static readonly Dictionary<string, int> RazerSegmentsBySku = new(StringComparer.OrdinalIgnoreCase)
    {
        ["H619A"] = 20, ["H619B"] = 20, ["H619C"] = 20, ["H619D"] = 20, ["H619E"] = 20, ["H619Z"] = 20,
        ["H618A"] = 20, ["H618C"] = 20, ["H618E"] = 20, ["H618F"] = 20,
    };
    private const int DefaultRazerSegments = 20;

    private readonly GoveeLanClient _client;
    // Parsed Extra payloads, keyed by device id, invalidated on change.
    private readonly ConcurrentDictionary<string, (string Raw, GoveeExtra Parsed)> _extras = new();
    // Devices in razer (realtime) mode → TickCount64 of the last enable send.
    private readonly ConcurrentDictionary<string, long> _razerArmedAt = new();

    public GoveeDriver(GoveeLanClient client) => _client = client;

    public string Brand => "govee";

    public int MinIntervalMs(SmartLight dev) => GetExtra(dev)?.Razer == true ? RazerIntervalMs : ControlIntervalMs;

    // Each Govee device is its own controller.
    public string RateLimitKey(SmartLight dev) => dev.Id;

    public async Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
    {
        var found = await _client.ScanAsync(2500, ct).ConfigureAwait(false);
        var result = new List<DiscoveredLight>(found.Count);
        foreach (var d in found)
            result.Add(new DiscoveredLight(Brand, d.Ip, DisplayName(d.Sku), d.Device));
        return result;
    }

    public async Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct)
    {
        // The streaming/control paths need an IPv4 literal (raw UDP, no DNS
        // per frame) - resolve a user-typed hostname once, here.
        var host = await LanHost.ResolveIpv4Async(target.Host, ct).ConfigureAwait(false);
        if (host is null)
            return new PairResult { Ok = false, Error = "host-not-found" };

        // No token exchange - "pairing" verifies the device actually answers on
        // the LAN (i.e. LAN Control is on) and captures its SKU capabilities.
        GoveeDeviceInfo? info = null;
        try { info = await _client.ProbeAsync(host, ProbeTimeoutMs, ct).ConfigureAwait(false); }
        catch { /* fall through to the status probe */ }

        string sku;
        string device;
        if (info is not null)
        {
            sku = info.Sku;
            device = info.Device;
        }
        else
        {
            // Unicast scan filtered? A devStatus answer still proves LAN
            // Control is enabled; capabilities fall back to the SKU-less default.
            var status = await _client.StatusAsync(host, ProbeTimeoutMs, ct).ConfigureAwait(false);
            if (status is null)
                return new PairResult { Ok = false, Error = "lan-control" };
            sku = "";
            device = target.StableKey is { Length: > 0 } k ? k : host;
        }

        var extra = new GoveeExtra
        {
            Sku = sku,
            // Unknown SKU (status-only pairing) defaults to control-only: a
            // bulb fed razer packets it ignores would show dead effects with
            // no fallback, while a strip on colorwc still works (averaged).
            Razer = sku.Length > 0 && !SingleColorSkus.Contains(sku),
            Segments = RazerSegmentsBySku.TryGetValue(sku, out var n) ? n : DefaultRazerSegments,
        };

        return new PairResult
        {
            Ok = true,
            Devices = new List<SmartLightConfig>
            {
                new()
                {
                    Id = $"govee:{device}",
                    Brand = Brand,
                    Name = target.Name is { Length: > 0 } && target.Name != Brand ? target.Name : DisplayName(sku),
                    Host = host,
                    StableKey = device,
                    Token = "",
                    Extra = JsonSerializer.Serialize(extra, GoveeJsonContext.Default.GoveeExtra),
                    Enabled = true,
                },
            },
        };
    }

    private static string DisplayName(string sku) => sku is { Length: > 0 } ? $"Govee {sku}" : "Govee";

    public LightFramePlan PlanFrames(SmartLight dev)
    {
        var extra = GetExtra(dev);
        if (extra?.Razer != true)
            return new LightFramePlan(16, AverageToSingle: true);
        // Strip ICs lie on a line: sample the canvas left→right at mid-height so
        // effects/screen-mirror map along the strip instead of a square grid.
        var n = Math.Max(1, extra.Segments);
        var u = new float[n];
        var v = new float[n];
        for (var i = 0; i < n; i++) { u[i] = n > 1 ? (float)i / (n - 1) : 0.5f; v[i] = 0.5f; }
        // The realtime ("razer"/DreamView) mode drops a manual color ~60s without
        // frames, so a static color must be streamed, not sent once.
        return new LightFramePlan(n, AverageToSingle: false, u, v, StaticNeedsStreaming: true);
    }

    public async Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct)
    {
        var extra = GetExtra(dev);

        if (frame.Zones is { } zones && frame.On && extra?.Razer == true)
        {
            var now = Environment.TickCount64;
            if (!_razerArmedAt.TryGetValue(dev.Id, out var armedAt))
            {
                // Cold entry (first frame, or after a power-off turned the strip
                // off). Firmware scales razer output by power+brightness, so set
                // those first. The strip ignores a razer enable that arrives while
                // its Wi-Fi/firmware is still waking from the power-on: bench shows
                // ~300ms gaps after turn-on let the enable take, while sending them
                // back-to-back leaves it stuck in its built-in scene.
                await _client.TurnAsync(dev.Host, on: true, ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                await _client.BrightnessAsync(dev.Host, 100, ct).ConfigureAwait(false);
                await Task.Delay(300, ct).ConfigureAwait(false);
                await _client.RazerAsync(dev.Host, GoveePackets.RazerModeBase64(enable: true), ct).ConfigureAwait(false);
                _razerArmedAt[dev.Id] = now;
            }
            else if (now - armedAt > RazerRearmMs)
            {
                // Periodic re-assert so a dropped enable self-heals; enable only,
                // no turn/brightness churn that would bounce the strip out of
                // DreamView mid-stream.
                await _client.RazerAsync(dev.Host, GoveePackets.RazerModeBase64(enable: true), ct).ConfigureAwait(false);
                _razerArmedAt[dev.Id] = now;
            }
            var count = Math.Min(zones.Length / 3, Math.Max(1, extra.Segments));
            await _client.RazerAsync(dev.Host,
                GoveePackets.RazerFrameBase64(zones, count, frame.Brightness01, gradient: true), ct).ConfigureAwait(false);
            return;
        }

        // Static path - leave realtime mode first so colorwc shows again.
        if (_razerArmedAt.TryRemove(dev.Id, out _))
            await _client.RazerAsync(dev.Host, GoveePackets.RazerModeBase64(enable: false), ct).ConfigureAwait(false);

        if (!frame.On)
        {
            await _client.TurnAsync(dev.Host, on: false, ct).ConfigureAwait(false);
            return;
        }
        await _client.TurnAsync(dev.Host, on: true, ct).ConfigureAwait(false);
        await _client.BrightnessAsync(dev.Host,
            Math.Clamp((int)Math.Round(frame.Brightness01 * 100), 1, 100), ct).ConfigureAwait(false);
        await _client.ColorAsync(dev.Host, frame.R, frame.G, frame.B, kelvin: 0, ct).ConfigureAwait(false);
    }

    public async Task IdentifyAsync(SmartLight dev, CancellationToken ct)
    {
        // No identify command in the LAN API - blink by toggling power, then
        // restore the state the device reported before the blink.
        var status = await _client.StatusAsync(dev.Host, 1500, ct).ConfigureAwait(false);
        var wasOn = status?.OnOff != 0;
        for (var i = 0; i < 2; i++)
        {
            await _client.TurnAsync(dev.Host, on: false, ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            await _client.TurnAsync(dev.Host, on: true, ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        if (!wasOn) await _client.TurnAsync(dev.Host, on: false, ct).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(SmartLight dev, CancellationToken ct)
    {
        try { return await _client.StatusAsync(dev.Host, 1500, ct).ConfigureAwait(false) is not null; }
        catch { return false; }
    }

    private GoveeExtra? GetExtra(SmartLight dev)
    {
        if (string.IsNullOrEmpty(dev.Extra)) return null;
        if (_extras.TryGetValue(dev.Id, out var cached) && cached.Raw == dev.Extra) return cached.Parsed;
        try
        {
            var parsed = JsonSerializer.Deserialize(dev.Extra, GoveeJsonContext.Default.GoveeExtra);
            if (parsed is null) return null;
            _extras[dev.Id] = (dev.Extra, parsed);
            return parsed;
        }
        catch { return null; }
    }
}
