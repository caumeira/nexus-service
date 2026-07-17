using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Mcp.Assistant;

/// <summary>
/// Typed client for the subset of Ollama's local HTTP API the runtime manager and
/// the agentic loop need. An interface so <see cref="OllamaRuntimeManager"/> and
/// <see cref="LocalAssistant"/> can be exercised against a fake in tests while the
/// real implementation talks to a real (managed or system) Ollama server.
/// </summary>
public interface IOllamaClient
{
    int Port { get; }

    /// <summary>Null on any failure (connection refused, timeout, non-200) - the
    /// readiness/detection probe, never throws.</summary>
    Task<string?> TryGetVersionAsync(CancellationToken ct);

    Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct);

    Task<IReadOnlyList<OllamaModelInfo>> ListLoadedModelsAsync(CancellationToken ct);

    /// <summary>Streams NDJSON progress lines to <paramref name="onProgress"/> as they
    /// arrive. Throws if the stream reports an error or the request fails.</summary>
    Task PullModelAsync(string model, Action<OllamaPullStatus> onProgress, CancellationToken ct);

    /// <summary>True on 200, false on 404 (model not installed).</summary>
    Task<bool> DeleteModelAsync(string model, CancellationToken ct);

    Task<OllamaChatResponse> ChatAsync(
        string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDef>? tools, CancellationToken ct);
}

public sealed class OllamaClient : IOllamaClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OllamaClient(HttpClient http, int port)
    {
        _http = http;
        Port = port;
        _baseUrl = $"http://127.0.0.1:{port}";
    }

    public int Port { get; }

    public async Task<string?> TryGetVersionAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/api/version", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            var body = await resp.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaVersionResponse, ct)
                .ConfigureAwait(false);
            return body?.Version;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaTagsResponse, ct)
            .ConfigureAwait(false);
        return body?.Models ?? new List<OllamaModelInfo>();
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> ListLoadedModelsAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/ps", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaTagsResponse, ct)
            .ConfigureAwait(false);
        return body?.Models ?? new List<OllamaModelInfo>();
    }

    public async Task PullModelAsync(string model, Action<OllamaPullStatus> onProgress, CancellationToken ct)
    {
        var body = new OllamaPullRequest { Model = model, Stream = true };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull")
        {
            Content = JsonContent.Create(body, OllamaJsonContext.Default.OllamaPullRequest),
        };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 } line)
        {
            var status = JsonSerializer.Deserialize(line, OllamaJsonContext.Default.OllamaPullStatus);
            if (status is null)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(status.Error))
            {
                throw new InvalidOperationException($"Pulling {model} failed: {status.Error}");
            }
            onProgress(status);
        }
    }

    public async Task<bool> DeleteModelAsync(string model, CancellationToken ct)
    {
        var body = new OllamaDeleteRequest { Model = model };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/delete")
        {
            Content = JsonContent.Create(body, OllamaJsonContext.Default.OllamaDeleteRequest),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<OllamaChatResponse> ChatAsync(
        string model, IReadOnlyList<OllamaChatMessage> messages, IReadOnlyList<OllamaToolDef>? tools, CancellationToken ct)
    {
        var body = new OllamaChatRequest
        {
            Model = model,
            Messages = new List<OllamaChatMessage>(messages),
            Tools = tools is null ? null : new List<OllamaToolDef>(tools),
            Stream = false,
        };
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/chat", body, OllamaJsonContext.Default.OllamaChatRequest, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaChatResponse, ct)
            .ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Ollama /api/chat returned an empty body.");
    }
}
