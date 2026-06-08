using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart.Drivers.Hue;

/// <summary>
/// HTTP(S) client for a Philips Hue bridge. The bridge serves a self-signed
/// certificate (CN = bridge id), so we accept the server cert for these calls —
/// scoped: this client only ever talks to LAN Hue bridges and Philips' own
/// discovery endpoint, both user-initiated. Uses CLIP v2 for enumerate/control
/// and the legacy /api endpoints for pairing + unauthenticated config.
/// </summary>
public sealed class HueBridgeClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                // Hue bridges present a self-signed cert; trust it for these LAN calls.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>Philips cloud discovery — returns bridges seen from this WAN IP.</summary>
    public async Task<List<HueDiscoveryEntry>> CloudDiscoverAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync("https://discovery.meethue.com", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, HueJsonContext.Default.ListHueDiscoveryEntry, ct)
            .ConfigureAwait(false) ?? new List<HueDiscoveryEntry>();
    }

    /// <summary>Unauthenticated bridge config (name + bridge id). Works without
    /// pairing, so it doubles as a reachability probe.</summary>
    public async Task<HueBridgeConfig?> GetBridgeConfigAsync(string host, CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://{host}/api/0/config", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, HueJsonContext.Default.HueBridgeConfig, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Attempt to create an app key. Before the link button is pressed
    /// the bridge returns an error item (type 101); after, a success item with
    /// the username (app key).</summary>
    public async Task<HueApiItem?> PairAsync(string host, CancellationToken ct)
    {
        var body = new HuePairBody { DeviceType = "nexus#service", GenerateClientKey = true };
        var json = JsonSerializer.Serialize(body, HueJsonContext.Default.HuePairBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"https://{host}/api", content, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var items = await JsonSerializer.DeserializeAsync(stream, HueJsonContext.Default.ListHueApiItem, ct)
            .ConfigureAwait(false);
        return items is { Count: > 0 } ? items[0] : null;
    }

    /// <summary>CLIP v2 light list.</summary>
    public async Task<List<HueLight>> GetLightsAsync(string host, string appKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/clip/v2/resource/light");
        req.Headers.Add("hue-application-key", appKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync(stream, HueJsonContext.Default.HueV2LightResponse, ct)
            .ConfigureAwait(false);
        return parsed?.Data ?? new List<HueLight>();
    }

    /// <summary>CLIP v2 light update (on/off, dimming, color).</summary>
    public async Task UpdateLightAsync(string host, string appKey, string rid, HueLightUpdate update, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(update, HueJsonContext.Default.HueLightUpdate);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"https://{host}/clip/v2/resource/light/{rid}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("hue-application-key", appKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Trigger the bridge's identify action (the lamp breathes).</summary>
    public async Task IdentifyAsync(string host, string appKey, string rid, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new HueIdentifyUpdate(), HueJsonContext.Default.HueIdentifyUpdate);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"https://{host}/clip/v2/resource/light/{rid}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("hue-application-key", appKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        // Identify failures are non-fatal; don't throw.
    }

    // ── Entertainment (CLIP v2) ───────────────────────────────────────────────

    public async Task<List<HueEntConfig>> GetEntertainmentConfigsAsync(string host, string appKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/clip/v2/resource/entertainment_configuration");
        req.Headers.Add("hue-application-key", appKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync(stream, HueJsonContext.Default.HueEntConfigResponse, ct).ConfigureAwait(false);
        return parsed?.Data ?? new List<HueEntConfig>();
    }

    public async Task<List<HueEntService>> GetEntertainmentServicesAsync(string host, string appKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/clip/v2/resource/entertainment");
        req.Headers.Add("hue-application-key", appKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync(stream, HueJsonContext.Default.HueEntServiceResponse, ct).ConfigureAwait(false);
        return parsed?.Data ?? new List<HueEntService>();
    }

    /// <summary>Start or stop streaming on an entertainment configuration
    /// (action = "start" | "stop").</summary>
    public async Task SetEntertainmentActionAsync(string host, string appKey, string configId, string action, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new HueEntAction { Action = action }, HueJsonContext.Default.HueEntAction);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"https://{host}/clip/v2/resource/entertainment_configuration/{configId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("hue-application-key", appKey);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }
}
