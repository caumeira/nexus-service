using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Integrations.HomeAssistant;

/// <summary>Thin HTTP wrapper for the Home Assistant REST API.</summary>
public sealed class HomeAssistantClient
{
    private readonly IHttpClientFactory _factory;

    public HomeAssistantClient(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Returns null on success. 401 = invalid token; other failures = unreachable URL.</summary>
    public async Task<string?> ValidateAsync(string url, string token, CancellationToken ct)
    {
        try
        {
            using var http = CreateClient(token);
            var resp = await http.GetAsync($"{url.TrimEnd('/')}/api/", ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return "invalid token";
            }
            resp.EnsureSuccessStatusCode();
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public async Task<JsonElement[]> GetStatesAsync(string url, string token, CancellationToken ct)
    {
        using var http = CreateClient(token);
        var resp = await http.GetAsync($"{url.TrimEnd('/')}/api/states", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new JsonElement[root.GetArrayLength()];
        int i = 0;
        foreach (var el in root.EnumerateArray())
        {
            result[i++] = el.Clone();
        }
        return result;
    }

    /// <summary>Returns null when the entity is not found (404).</summary>
    public async Task<JsonElement?> GetStateAsync(string url, string token, string entityId, CancellationToken ct)
    {
        using var http = CreateClient(token);
        var resp = await http.GetAsync($"{url.TrimEnd('/')}/api/states/{entityId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task CallServiceAsync(
        string url, string token, string domain, string service, string bodyJson, CancellationToken ct)
    {
        using var http = CreateClient(token);
        using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{url.TrimEnd('/')}/api/services/{domain}/{service}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    private HttpClient CreateClient(string token)
    {
        var http = _factory.CreateClient("HomeAssistant");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }
}
