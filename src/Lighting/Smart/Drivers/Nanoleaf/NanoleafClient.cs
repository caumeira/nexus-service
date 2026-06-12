using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Smart.Drivers.Nanoleaf;

/// <summary>
/// HTTP client for the Nanoleaf Open API — plain HTTP on the LAN, token in the
/// path (<c>/api/v1/&lt;token&gt;/…</c>). Shared by panel products and Matter
/// WiFi Essentials; both expose the same endpoints on the same port.
/// </summary>
public sealed class NanoleafClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    private static string Base(string host, int port, string token)
        => $"http://{host}:{port}/api/v1/{token}";

    /// <summary>Create an auth token. Returns null when the device is not in
    /// pairing mode (the API answers 401/403 until the user opens the window by
    /// holding the power button / "Connect to API" in the app).</summary>
    public async Task<string?> CreateTokenAsync(string host, int port, CancellationToken ct)
    {
        using var resp = await Http.PostAsync($"http://{host}:{port}/api/v1/new", content: null, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return null;
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var auth = await JsonSerializer.DeserializeAsync(stream, NanoleafJsonContext.Default.NanoleafAuthResponse, ct)
            .ConfigureAwait(false);
        return string.IsNullOrEmpty(auth?.AuthToken) ? null : auth!.AuthToken;
    }

    /// <summary>Full controller info (name, serial, model, panel layout).</summary>
    public async Task<NanoleafInfo?> GetInfoAsync(string host, int port, string token, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(Base(host, port, token), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, NanoleafJsonContext.Default.NanoleafInfo, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Essentials-only LED count (GET /length). Null when the endpoint
    /// doesn't exist (panel products).</summary>
    public async Task<int?> GetLedCountAsync(string host, int port, string token, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(Base(host, port, token) + "/length", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var len = await JsonSerializer.DeserializeAsync(stream, NanoleafJsonContext.Default.NanoleafLengthResponse, ct)
            .ConfigureAwait(false);
        return len?.NumLeds;
    }

    public async Task PutStateAsync(string host, int port, string token, NanoleafStateWrite state, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(state, NanoleafJsonContext.Default.NanoleafStateWrite);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.PutAsync(Base(host, port, token) + "/state", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Switch the controller into External Control v2 (UDP streaming).</summary>
    public async Task EnableExtControlAsync(string host, int port, string token, CancellationToken ct)
    {
        var body = new NanoleafEffectsWrite
        {
            Write = new NanoleafWriteCommand { Command = "display", AnimType = "extControl", ExtControlVersion = "v2" },
        };
        var json = JsonSerializer.Serialize(body, NanoleafJsonContext.Default.NanoleafEffectsWrite);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.PutAsync(Base(host, port, token) + "/effects", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Revoke an auth token (DELETE /api/v1/&lt;token&gt;) — used to
    /// clean up when pairing fails after token creation, so abandoned tokens
    /// don't accumulate on the controller.</summary>
    public async Task DeleteTokenAsync(string host, int port, string token, CancellationToken ct)
    {
        using var resp = await Http.DeleteAsync(Base(host, port, token), ct).ConfigureAwait(false);
        // Best-effort; the controller also caps stored tokens itself.
    }

    /// <summary>Flash the panels so the user can spot the controller. Not
    /// supported on Essentials — failures are non-fatal.</summary>
    public async Task IdentifyAsync(string host, int port, string token, CancellationToken ct)
    {
        using var resp = await Http.PutAsync(Base(host, port, token) + "/identify", content: null, ct).ConfigureAwait(false);
        // Non-fatal; Essentials answers 404 here.
    }

    /// <summary>Cheap reachability + token validity probe.</summary>
    public async Task<bool> PingAsync(string host, int port, string token, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(Base(host, port, token) + "/state/on", ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }
}
