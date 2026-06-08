using System;
using System.Collections.Generic;
using System.Text.Json;
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

    // All bulbs on a bridge share one rate budget — keyed by bridge host — so a
    // many-light effect can't flood the bridge (it handles ~10 cmds/s total).
    public string RateLimitKey(SmartLight dev) => "hue:" + dev.Host;

    public async Task<IReadOnlyList<DiscoveredLight>> DiscoverAsync(CancellationToken ct)
    {
        // Dedup by HOST (IP): cloud discovery and mDNS both surface the same
        // bridge, and the same IP is always the same bridge — so keying on host
        // is duplicate-proof even when the config probe (which yields the
        // bridge id) races or fails.
        var byHost = new Dictionary<string, DiscoveredLight>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var e in await _client.CloudDiscoverAsync(ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(e.InternalIpAddress))
                    await AddHostAsync(byHost, e.InternalIpAddress, e.Id, ct).ConfigureAwait(false);
            }
        }
        catch { /* cloud unreachable — fall through to mDNS */ }

        try
        {
            foreach (var host in await _lan.MdnsHostsAsync(MdnsService, 2000, ct).ConfigureAwait(false))
            {
                await AddHostAsync(byHost, host, "", ct).ConfigureAwait(false);
            }
        }
        catch { /* mDNS best-effort */ }

        return new List<DiscoveredLight>(byHost.Values);
    }

    private async Task AddHostAsync(Dictionary<string, DiscoveredLight> sink, string host, string idHint, CancellationToken ct)
    {
        // Already found this IP (e.g. cloud then mDNS) — skip the redundant probe.
        if (sink.ContainsKey(host)) return;

        HueBridgeConfig? cfg = null;
        try { cfg = await _client.GetBridgeConfigAsync(host, ct).ConfigureAwait(false); }
        catch { /* unreachable host — still record under its IP below */ }

        var bridgeId = !string.IsNullOrEmpty(cfg?.BridgeId) ? cfg!.BridgeId
            : !string.IsNullOrEmpty(idHint) ? idHint
            : host;
        var name = !string.IsNullOrWhiteSpace(cfg?.Name) ? cfg!.Name : "Philips Hue Bridge";
        sink[host] = new DiscoveredLight(Brand, host, name, bridgeId);
    }

    public async Task<bool> PingAsync(SmartLight dev, CancellationToken ct)
    {
        try
        {
            var cfg = await _client.GetBridgeConfigAsync(dev.Host, ct).ConfigureAwait(false);
            return cfg is not null && !string.IsNullOrEmpty(cfg.BridgeId);
        }
        catch { return false; }
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
            // clientkey is the DTLS pre-shared key for Entertainment streaming;
            // it's only returned here at pairing, so capture it now.
            var clientKey = s.ClientKey ?? "";
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
                    Extra = BuildExtra(l.Id, clientKey),
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
        var (rid, _) = ParseExtra(dev.Extra);
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(rid)) return;

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
                // Snap instantly — without this the lamp applies its default
                // ~400ms fade, smearing every streamed frame.
                Dynamics = new HueDynamics { Duration = 0 },
            };
        }
        await _client.UpdateLightAsync(dev.Host, appKey, rid, update, ct).ConfigureAwait(false);
    }

    public async Task IdentifyAsync(SmartLight dev, CancellationToken ct)
    {
        var appKey = SecretProtector.Unprotect(dev.Token);
        var (rid, _) = ParseExtra(dev.Extra);
        if (string.IsNullOrEmpty(appKey) || string.IsNullOrEmpty(rid)) return;
        await _client.IdentifyAsync(dev.Host, appKey, rid, ct).ConfigureAwait(false);
    }

    // Extra encodes the v2 light rid + the bridge clientkey (DTLS PSK). New
    // entries are JSON; legacy entries were the bare rid string.
    internal static string BuildExtra(string rid, string clientKey)
        => JsonSerializer.Serialize(new HueDeviceExtra { Rid = rid, ClientKey = clientKey }, HueJsonContext.Default.HueDeviceExtra);

    internal static (string rid, string clientKey) ParseExtra(string extra)
    {
        if (string.IsNullOrEmpty(extra)) return ("", "");
        if (extra[0] == '{')
        {
            try
            {
                var e = JsonSerializer.Deserialize(extra, HueJsonContext.Default.HueDeviceExtra);
                if (e is not null) return (e.Rid, e.ClientKey);
            }
            catch { /* fall through to legacy */ }
        }
        return (extra, ""); // legacy: bare rid, no clientkey (re-pair to capture)
    }
}
