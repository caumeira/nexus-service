using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Body of <c>POST /widgets-api/proxy</c>. The host runs the actual fetch
/// after validating the URL against the widget's <c>capabilities.net.fetch</c>
/// allowlist; the response is size-capped + decoded into <see cref="WidgetProxyResponse"/>.
/// </summary>
public sealed class WidgetProxyRequest
{
    public string WidgetId { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Method { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string? Body { get; set; }
    /// <summary>
    /// Hosts the caller believes the manifest allowlists. Defense in depth:
    /// the host also reads the registry's manifest and rejects when these
    /// disagree, so a tampered client can't widen its own allowlist.
    /// </summary>
    public List<string>? AllowedHosts { get; set; }
}

public sealed class WidgetProxyResponse
{
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string StatusText { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>
    /// JSON-decoded body when the upstream response declared
    /// <c>application/json</c>. Null otherwise (the raw text body
    /// lives in <see cref="BodyText"/>). Truncated to
    /// <see cref="WidgetProxyService.MaxBodyBytes"/>.
    /// </summary>
    /// <remarks>
    /// Nullable so the source-generated serializer never has to emit a
    /// default-initialised <c>JsonElement</c> (kind=Undefined), which throws.
    /// </remarks>
    public JsonElement? Body { get; set; }
    /// <summary>Raw text body when the upstream wasn't JSON.</summary>
    public string? BodyText { get; set; }
    public string? Error { get; set; }
}
