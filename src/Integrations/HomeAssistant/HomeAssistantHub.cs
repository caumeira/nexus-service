using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Persistence;
using Nexus.Service.Security;
using Nexus.Service.Sockets;

namespace Nexus.Service.Integrations.HomeAssistant;

public sealed class HomeAssistantHub : BackgroundService
{
    // Network-reconnect backoff bounds. Clean WS close uses BackoffMin to prevent storm.
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(30);

    private readonly IConfigStore _store;
    private readonly HomeAssistantClient _client;
    private readonly MultiplexHub _hub;
    private readonly ILogger<HomeAssistantHub> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, HaEntityDto> _cache = new(StringComparer.OrdinalIgnoreCase);
    private HaRegistryMaps _maps = new(); // guarded by _lock
    private bool _connected;
    private string _error = "";

    // Replaced atomically in SignalReconfigure; old CTS is cancelled to wake any
    // Task.Delay or RunConnectionAsync linked to it via iterCts.
    private volatile CancellationTokenSource _reconfigureCts = new();

    public HomeAssistantHub(
        IConfigStore store,
        HomeAssistantClient client,
        MultiplexHub hub,
        ILogger<HomeAssistantHub> logger)
    {
        _store = store;
        _client = client;
        _hub = hub;
        _logger = logger;
    }

    public HaConfigResponse GetConfigResponse()
    {
        var settings = _store.Load().HomeAssistant;
        bool configured = HasToken(settings);
        bool connected;
        string error;
        lock (_lock)
        {
            connected = _connected;
            error = _error;
        }
        return new HaConfigResponse
        {
            Url = settings.Url,
            Configured = configured,
            Connected = connected,
            Error = error,
        };
    }

    public async Task<HaConfigSetResponse> SetConfigAsync(string url, string token, CancellationToken ct)
    {
        url = url.Trim().TrimEnd('/');
        token = token.Trim();

        if (url.Length == 0 || token.Length == 0)
        {
            return new HaConfigSetResponse { Ok = false, Error = "url and token are required" };
        }

        var validateError = await _client.ValidateAsync(url, token, ct);
        if (validateError is not null)
        {
            return new HaConfigSetResponse { Ok = false, Error = validateError };
        }

        _store.Update(s =>
        {
            s.HomeAssistant.Url = url;
            s.HomeAssistant.Token = SecretProtector.Protect(token);
            s.HomeAssistant.Enabled = true;
        });

        // Wake the background loop immediately regardless of whether it is in
        // the unconfigured poll or an error backoff.
        SignalReconfigure();

        // REST validation passed, so the URL+token are known-good.
        return new HaConfigSetResponse { Ok = true, Connected = true };
    }

    public HaEntitiesResponse GetEntitiesResponse()
    {
        var settings = _store.Load().HomeAssistant;
        bool configured = HasToken(settings);
        bool connected;
        string error;
        List<HaEntityDto> entities;
        lock (_lock)
        {
            connected = _connected;
            error = _error;
            entities = new List<HaEntityDto>(_cache.Values);
        }
        return new HaEntitiesResponse
        {
            Configured = configured,
            Connected = connected,
            Error = error,
            Entities = entities,
        };
    }

    /// <summary>
    /// Calls the HA service, reads back the updated state, refreshes the cache,
    /// and broadcasts. Returns null when the hub is not configured, the entity is
    /// unknown, or the HA call fails.
    /// </summary>
    public async Task<HaEntityDto?> SetEntityAsync(HaSetEntityBody body, CancellationToken ct)
    {
        var cfg = ResolveRuntimeConfig();
        if (!cfg.IsConfigured)
        {
            return null;
        }

        var entityId = body.EntityId.Trim();
        HaEntityDto? entity;
        lock (_lock)
        {
            _cache.TryGetValue(entityId, out entity);
        }
        if (entity is null)
        {
            return null;
        }

        // Clamp inputs to protocol-valid ranges.
        var brightnessPct = body.BrightnessPct.HasValue
            ? (int?)Math.Clamp(body.BrightnessPct.Value, 0, 100)
            : null;
        var rgb = body.Rgb;
        if (rgb is not null && rgb.Length == 3)
        {
            rgb = new int[]
            {
                Math.Clamp(rgb[0], 0, 255),
                Math.Clamp(rgb[1], 0, 255),
                Math.Clamp(rgb[2], 0, 255),
            };
        }
        var colorTempK = body.ColorTempK.HasValue
            ? (int?)Math.Clamp(body.ColorTempK.Value, 1000, 10000)
            : null;

        var domain = entity.Domain;
        string service;
        string bodyJson;

        if (domain == "switch")
        {
            service = body.On == false ? "turn_off" : "turn_on";
            bodyJson = BuildServiceBody(entityId, null, null, null);
        }
        else
        {
            // light
            if (body.On == false)
            {
                service = "turn_off";
                bodyJson = BuildServiceBody(entityId, null, null, null);
            }
            else
            {
                service = "turn_on";
                bodyJson = BuildServiceBody(entityId, brightnessPct, rgb, colorTempK);
            }
        }

        try
        {
            await _client.CallServiceAsync(cfg.Url, cfg.Token, domain, service, bodyJson, ct);

            var stateEl = await _client.GetStateAsync(cfg.Url, cfg.Token, entityId, ct);
            if (stateEl is null)
            {
                return null;
            }

            var updated = NormalizeEntity(stateEl.Value);
            if (updated is null)
            {
                return null;
            }

            lock (_lock)
            {
                updated.Area = _maps.Resolve(entityId);
                _cache[entityId] = updated;
            }
            PanelTopics.BroadcastHomeAssistant(_hub);
            return updated;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Home Assistant SetEntity {Id} failed: {Msg}", entityId, ex.Message);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var backoff = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            // iterCts links both the host stop token and the current reconfigure
            // signal so any Task.Delay or WS loop wakes on either.
            using var iterCts = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _reconfigureCts.Token);
            var ct = iterCts.Token;

            var cfg = ResolveRuntimeConfig();
            if (!cfg.IsConfigured)
            {
                SetState(connected: false, error: "");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Reconfigure signal woke the poll.
                }
                continue;
            }

            try
            {
                await RunConnectionAsync(cfg.Url, cfg.Token, ct);
                // Clean close: apply minimum backoff to avoid reconnect storm on a
                // flapping HA server.
                backoff = BackoffMin;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Reconfigure triggered; reconnect immediately with new config.
                backoff = TimeSpan.Zero;
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Home Assistant connection error: {Msg}", ex.Message);
                SetState(connected: false, error: ex.Message);
                // Network-reconnect backoff: exponential, bounded.
                backoff = backoff == TimeSpan.Zero
                    ? BackoffMin
                    : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, BackoffMax.TotalSeconds));
            }

            if (!stoppingToken.IsCancellationRequested && backoff > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Reconfigure woke the backoff; reconnect immediately.
                    backoff = TimeSpan.Zero;
                }
            }
        }

        SetState(connected: false, error: "");
    }

    private async Task RunConnectionAsync(string url, string token, CancellationToken ct)
    {
        var wsUrl = DeriveWsUrl(url);

        await using var ws = new HomeAssistantWebSocket();
        await ws.ConnectAsync(wsUrl, token, ct);

        var maps = await ws.FetchRegistriesAsync(ct);

        // REST snapshot after WS subscription + registry fetch so no state events
        // are missed in the gap between subscribe and the REST call.
        var states = await _client.GetStatesAsync(url, token, ct);
        lock (_lock)
        {
            _maps = maps;
            _cache.Clear();
            foreach (var el in states)
            {
                var dto = NormalizeEntity(el);
                if (dto is not null)
                {
                    dto.Area = _maps.Resolve(dto.Id);
                    _cache[dto.Id] = dto;
                }
            }
            _connected = true;
            _error = "";
        }
        PanelTopics.BroadcastHomeAssistant(_hub);

        while (!ct.IsCancellationRequested)
        {
            var msg = await ws.ReceiveNextAsync(ct);
            if (msg is null)
            {
                break;
            }

            if (msg.Value.IsRegistryUpdate)
            {
                var newMaps = await ws.FetchRegistriesAsync(ct);
                UpdateMapsAndRebuildAreas(newMaps);
                PanelTopics.BroadcastHomeAssistant(_hub);
                continue;
            }

            var entityId = msg.Value.EntityId;
            var dot = entityId.IndexOf('.');
            if (dot < 0)
            {
                continue;
            }
            var domain = entityId.Substring(0, dot);
            if (domain != "light" && domain != "switch")
            {
                continue;
            }

            var updated = NormalizeEntity(msg.Value.NewState);
            if (updated is null)
            {
                continue;
            }

            lock (_lock)
            {
                updated.Area = _maps.Resolve(entityId);
                _cache[entityId] = updated;
            }
            PanelTopics.BroadcastHomeAssistant(_hub);
        }

        SetState(connected: false, error: "");
    }

    private void UpdateMapsAndRebuildAreas(HaRegistryMaps maps)
    {
        lock (_lock)
        {
            _maps = maps;
            foreach (var dto in _cache.Values)
            {
                dto.Area = _maps.Resolve(dto.Id);
            }
        }
    }

    private void SignalReconfigure()
    {
        try
        {
            Interlocked.Exchange(ref _reconfigureCts, new CancellationTokenSource()).Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private void SetState(bool connected, string error)
    {
        lock (_lock)
        {
            _connected = connected;
            _error = error;
        }
    }

    private RuntimeConfig ResolveRuntimeConfig()
    {
        var settings = _store.Load().HomeAssistant;
        var url = settings.Url.Trim().TrimEnd('/');
        var token = SecretProtector.Unprotect(settings.Token).Trim();
        var configured = settings.Enabled && url.Length > 0 && token.Length > 0;
        return new RuntimeConfig(url, token, configured);
    }

    private static bool HasToken(HomeAssistantSettings settings)
    {
        return SecretProtector.Unprotect(settings.Token).Trim().Length > 0;
    }

    private static string DeriveWsUrl(string httpUrl)
    {
        var uri = new Uri(httpUrl.TrimEnd('/'));
        var scheme = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        return $"{scheme}://{uri.Authority}/api/websocket";
    }

    private static HaEntityDto? NormalizeEntity(JsonElement stateEl)
    {
        if (!stateEl.TryGetProperty("entity_id", out var idEl))
        {
            return null;
        }
        var entityId = idEl.GetString() ?? "";
        var dot = entityId.IndexOf('.');
        if (dot < 0)
        {
            return null;
        }
        var domain = entityId.Substring(0, dot);
        if (domain != "light" && domain != "switch")
        {
            return null;
        }

        var state = stateEl.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "" : "";
        var name = entityId;
        int brightnessPct = 0;
        bool supportsBrightness = false, supportsColor = false, supportsColorTemp = false;
        int[]? rgb = null;
        int colorTempK = 0;

        if (stateEl.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.TryGetProperty("friendly_name", out var fnEl) &&
                fnEl.ValueKind == JsonValueKind.String)
            {
                name = fnEl.GetString() ?? entityId;
            }

            if (domain == "light")
            {
                if (attrs.TryGetProperty("brightness", out var bEl) &&
                    bEl.ValueKind == JsonValueKind.Number)
                {
                    brightnessPct = (int)Math.Round(bEl.GetDouble() / 255.0 * 100.0);
                }

                if (attrs.TryGetProperty("supported_color_modes", out var modesEl) &&
                    modesEl.ValueKind == JsonValueKind.Array)
                {
                    var modes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var m in modesEl.EnumerateArray())
                    {
                        var s = m.GetString();
                        if (s is not null)
                        {
                            modes.Add(s);
                        }
                    }

                    supportsColor = modes.Contains("hs") || modes.Contains("rgb") ||
                                    modes.Contains("rgbw") || modes.Contains("rgbww") ||
                                    modes.Contains("xy");
                    supportsColorTemp = modes.Contains("color_temp");
                    var onlyOnOff = modes.Count == 1 && modes.Contains("onoff");
                    supportsBrightness = modes.Count > 0 && !onlyOnOff;
                }

                if (attrs.TryGetProperty("rgb_color", out var rgbEl) &&
                    rgbEl.ValueKind == JsonValueKind.Array)
                {
                    var vals = new int[3];
                    int i = 0;
                    foreach (var c in rgbEl.EnumerateArray())
                    {
                        if (i >= 3)
                        {
                            break;
                        }
                        if (c.ValueKind != JsonValueKind.Number)
                        {
                            break;
                        }
                        vals[i++] = (int)Math.Round(c.GetDouble());
                    }
                    if (i == 3)
                    {
                        rgb = vals;
                    }
                }

                if (attrs.TryGetProperty("color_temp_kelvin", out var ctEl) &&
                    ctEl.ValueKind == JsonValueKind.Number)
                {
                    colorTempK = (int)Math.Round(ctEl.GetDouble());
                }
            }
        }

        return new HaEntityDto
        {
            Id = entityId,
            Name = name,
            Domain = domain,
            State = state,
            On = state == "on",
            Reachable = state != "unavailable",
            BrightnessPct = brightnessPct,
            SupportsBrightness = supportsBrightness,
            SupportsColor = supportsColor,
            SupportsColorTemp = supportsColorTemp,
            Rgb = rgb,
            ColorTempK = colorTempK,
            Area = "",
        };
    }

    private static string BuildServiceBody(string entityId, int? brightnessPct, int[]? rgb, int? colorTempK)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);
        w.WriteStartObject();
        w.WriteString("entity_id", entityId);
        if (brightnessPct.HasValue)
        {
            w.WriteNumber("brightness_pct", brightnessPct.Value);
        }
        if (rgb is not null && rgb.Length == 3)
        {
            w.WritePropertyName("rgb_color");
            w.WriteStartArray();
            w.WriteNumberValue(rgb[0]);
            w.WriteNumberValue(rgb[1]);
            w.WriteNumberValue(rgb[2]);
            w.WriteEndArray();
        }
        if (colorTempK.HasValue)
        {
            w.WriteNumber("color_temp_kelvin", colorTempK.Value);
        }
        w.WriteEndObject();
        w.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed record RuntimeConfig(string Url, string Token, bool IsConfigured);
}
