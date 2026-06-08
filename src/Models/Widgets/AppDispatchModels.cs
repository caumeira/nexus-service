using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Models.Widgets;

/// <summary>
/// Body of <c>POST /apps-api/dispatch</c>. The host-action registry
/// validates <see cref="Action"/> against the widget manifest's
/// <c>capabilities.dispatch</c> allowlist before invoking the
/// registered handler.
/// </summary>
public sealed class AppDispatchRequest
{
    public string AppId { get; set; } = "";
    public string Action { get; set; } = "";
    /// <summary>Action-specific argument bag. Each handler narrows it.</summary>
    public Dictionary<string, JsonElement>? Args { get; set; }
}

public sealed class AppDispatchResponse
{
    public bool Ok { get; set; }
    /// <summary>Result body, action-specific shape. Null on error.</summary>
    public JsonElement? Result { get; set; }
    public string? Error { get; set; }
}
