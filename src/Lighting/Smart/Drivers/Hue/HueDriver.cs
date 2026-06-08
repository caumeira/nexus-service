using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Smart.Discovery;
using Nexus.Service.Persistence;
using Nexus.Service.Security;

namespace Nexus.Service.Lighting.Smart.Drivers.Hue;

/// <summary>
/// Philips Hue driver. Discovers bridges (Philips cloud + mDNS), pairs via the
/// link-button flow, and controls/streams lights over CLIP v2. Hue bulbs are
/// single-color, so frames are averaged from a small canvas grid; the bridge
/// throttles REST, so we cap sends at ~10/s (Entertainment streaming is out of
/// scope — see plans/smart-lights-integration.md).
/// </summary>
public sealed class HueDriver : ILightDriver
{
    private const int MinSendIntervalMs = 100; // ~10 Hz — bridge REST ceiling.
    private const string MdnsService = "_hue._tcp";

    private readonly HueBridgeClient _client;
    private readonly LanDiscovery _lan;

    public HueDriver(HueBridgeClient client, LanDiscovery lan)
    {
        _client = client;
        _lan = lan;
    }

    public string Brand => "hue";

    public LightFramePlan PlanFrames(SmartLight dev) => new(LedCount: 16, AverageToSingle: true);

    public int MinIntervalMs(SmartLight dev) => MinSendIntervalMs;

    public async Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
    {
        var byKey = new Dictionary<string, DiscoveredLight>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var e in await _client.CloudDiscoverAsync(ct).ConfigureAwait(false))
                if (!string.IsNullOrWhiteSpace(e.InternalIpAddress))
                    await AddHostAsync(byKey, e.InternalIpAddress, e.Id, ct).ConfigureAwait(false);
        }
        catch { /* cloud unreachable — fall through to mDNS */ }

        try
        {
            foreach (var host in await _lan.MdnsHostsAsync(MdnsService, 2000, ct).ConfigureAwait(false))
                await AddHostAsync(byKey, host, "", ct).ConfigureAwait(false);
        }
        catch { /* mDNS best-effort */ }

        return new List<DiscoveredLight>(byKey.Values);
    }

    private async Task AddHostAsync(Dictionary<string, DiscoveredLight> sink, string host, string idHint, CancellationToken ct)
    {
        HueBridgeConfig? cfg = null;
        try { cfg = await _client.GetBridgeConfigAsync(host, ct).ConfigureAwait(false); }
        catch { /* unreachable host — skip below */ }

        var bridgeId = !string.IsNullOrEmpty(cfg?.BridgeId) ? cfg!.BridgeId
            : !string.IsNullOrEmpty(idHint) ? idHint
            : host;
        var name = !string.IsNullOrWhiteSpace(cfg?.Name) ? cfg!.Name : "Philips Hue Bridge";
        if (!sink.ContainsKey(bridgeId))
            sink[bridgeId] = new DiscoveredLight(Brand, host, name, bridgeId);
    }

    public async Task<PairResult> PairAsync(DiscoveredLight target, CancellationToken ct)
    {
        HueApiItem? item;
        try { item = await _client.PairAsync(target.Host, ct).ConfigureAwait(false); }
        catch (Exception ex) { return new PairResult { Ok = false, Error = ex.Message }; }

        if (item?.Success is { } s && !string.IsNullOrEmpty(s.Username))
        {
            var bridgeId = target.StableKey;
            if (string.IsNullOrEmpty(bridgeId))
            {
                try { bridgeId = (await _client.GetBridgeConfigAsync(target.Host, ct).ConfigureAwait(false))?.BridgeId ?? target.Host; }
                catch { bridgeId = target.Host; }
            }

            List<HueLight> lights;
            try { lights = await _client.GetLightsAsync(target.Host, s.Username, ct).ConfigureAwait(false); }
            catch (Exception ex) { return new PairResult { Ok = false, Error = "enumerate-failed: " + ex.Message }; }

            var token = SecretProtector.Protect(s.Username);
            var devices = new List<SmartLightConfig>();
            foreach (var l in lights)
            {
                if (!string.Equals(l.Type, "light", StringComparison.OrdinalIgnoreCase)) continue;
                devices.Add(new SmartLightConfig
                {
                    Id = $"hue:{bridgeId}:{l.Id}",
                    Brand = Brand,
                    Name = l.Metadata?.Name is { Length: > 0 } n ? n : "Hue light",
                    Host = target.Host,
                    StableKey = bridgeId,
                    Token = token,
                    Extra = l.Id,
                    Enabled = true,
                });
            }
            return new PairResult { Ok = true, Devices = devices };
        }

        var err = item?.Error;
        var isLink = err?.Type == 101
            || (err?.Description?.Contains("link button", StringComparison.OrdinalIgnoreCase) ?? false);
        return new PairResult { Ok = false, Error = isLink ? "link-button" : (err?.Description ?? "pair-failed") };
    }

    public async Task SendAsync(SmartLight dev, LightFrame frame, CancellationToken ct)
    {
        var appKey = SecretProtector.Unprotect(dev.Token);
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(dev.Extra)) return;

        HueLightUpdate update;
        if (!frame.On)
        {
            update = new HueLightUpdate { On = new HueOn { On = false } };
        }
        else
        {
            var value = ColorMath.Value(frame.R, frame.G, frame.B);
            var bri = Math.Clamp(frame.Brightness01 * value * 100.0, 0.0, 100.0);
            var (x, y) = ColorMath.RgbToXy(frame.R, frame.G, frame.B);
            update = new HueLightUpdate
            {
                On = new HueOn { On = true },
                Dimming = new HueDimming { Brightness = Math.Max(1.0, bri) },
                Color = (x > 0 || y > 0) ? new HueColor { Xy = new HueXy { X = x, Y = y } } : null,
            };
        }
        await _client.UpdateLightAsync(dev.Host, appKey, dev.Extra, update, ct).ConfigureAwait(false);
    }

    public async Task IdentifyAsync(SmartLight dev, CancellationToken ct)
    {
        var appKey = SecretProtector.Unprotect(dev.Token);
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(dev.Extra)) return;
        await _client.IdentifyAsync(dev.Host, appKey, dev.Extra, ct).ConfigureAwait(false);
    }
}
