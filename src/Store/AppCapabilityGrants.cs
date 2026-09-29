using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Store;

/// <summary>
/// A manifest's capabilities as flat grant strings (<c>dispatch:lighting.setMode</c>,
/// <c>net.fetch:api.example.com</c>, <c>appData</c>), the unit the user consents to.
/// The web client maps the same strings to labels.
/// </summary>
public static class AppCapabilityGrants
{
    public static SortedSet<string> From(AppManifestCapabilities? caps)
    {
        var grants = new SortedSet<string>(StringComparer.Ordinal);
        if (caps is null) return grants;
        foreach (var a in caps.Dispatch) AddScoped(grants, "dispatch", a);
        foreach (var h in caps.NetFetch) AddScoped(grants, "net.fetch", h.ToLowerInvariant());
        foreach (var p in caps.SensorsRead) AddScoped(grants, "sensors.read", p);
        foreach (var p in caps.MediaImport) AddScoped(grants, "mediaImport", p);
        if (caps.AppData) grants.Add("appData");
        if (caps.Audio) grants.Add("audio");
        if (caps.RgbRead) grants.Add("rgb.read");
        if (caps.RgbWrite) grants.Add("rgb.write");
        return grants;
    }

    /// <summary>Grants in <paramref name="requested"/> that <paramref name="approved"/> does not cover.</summary>
    public static List<string> Missing(IEnumerable<string> requested, IEnumerable<string>? approved)
    {
        var ok = new HashSet<string>(approved ?? Array.Empty<string>(), StringComparer.Ordinal);
        return requested.Where(g => !ok.Contains(g)).ToList();
    }

    private static void AddScoped(SortedSet<string> grants, string kind, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) grants.Add($"{kind}:{value.Trim()}");
    }
}
