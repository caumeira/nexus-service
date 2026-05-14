using System.Collections.Generic;
using System.Text.Json;

namespace Qos.Service.Models.Widgets;

/// <summary>
/// Listing payload returned by <c>GET /widgets-api/installed</c>. Carries
/// the manifest view tree + data sources so the dashboard can render the
/// widget without a second fetch.
/// </summary>
public sealed class WidgetInstalledListing
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public List<string> Surfaces { get; set; } = new();
    public WidgetManifestCapabilities Capabilities { get; set; } = new();
    public WidgetManifestViewport? Viewport { get; set; }
    public List<WidgetManifestSettingEntry> Settings { get; set; } = new();
    public List<string> Sizes { get; set; } = new();
    public string? DefaultSize { get; set; }
    public JsonElement View { get; set; }
    public Dictionary<string, WidgetManifestDataSource> Data { get; set; } = new();
    public List<WidgetManifestFont> Fonts { get; set; } = new();
    public JsonElement? Local { get; set; }
    public string Source { get; set; } = ""; // "dev" | "user" | "bundled"
    public bool Trusted { get; set; }        // true when source != "dev"
}

public sealed class WidgetInstalledListingResponse
{
    public List<WidgetInstalledListing> Widgets { get; set; } = new();
}

/// <summary>Marketplace catalogue entry: a widget that could be installed
/// (or already is) into the user widgets dir.</summary>
public sealed class WidgetCatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public string? IconUrl { get; set; }
    public List<string> Surfaces { get; set; } = new();
    public WidgetManifestCapabilities Capabilities { get; set; } = new();
    public string Source { get; set; } = ""; // "bundled" | "dev" | "user"
    public bool Installed { get; set; }      // true if a copy exists under user widgets
}

public sealed class WidgetCatalogResponse
{
    public List<WidgetCatalogEntry> Entries { get; set; } = new();
}

public sealed class WidgetInstallRequest
{
    public string Id { get; set; } = "";
}

public sealed class WidgetInstallResponse
{
    public bool Installed { get; set; }
    public string Id { get; set; } = "";
    public string? Error { get; set; }
}

/// <summary>
/// Result of <c>POST /widgets-api/installed/{id}/code-session</c>. The session
/// id is a short-lived URL-path token; the worker's module imports inherit it
/// automatically, so the host doesn't need to embed Bearer auth in module
/// import URLs.
/// </summary>
public sealed class WidgetCodeSessionResponse
{
    public string SessionId { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int ExpiresInSeconds { get; set; }
    public string? Error { get; set; }
}
