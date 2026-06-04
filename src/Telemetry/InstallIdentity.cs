using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Resolves the anonymous per-install id shared by every telemetry channel
/// (fleet heartbeat + product events) and enforces the single opt-out. When
/// CollectAnonymousData is off, the stored id is forgotten and null is
/// returned, so re-enabling later looks like a fresh install.
/// </summary>
internal static class InstallIdentity
{
    /// <returns>The anonymous install id, or null when the user has opted out.</returns>
    public static string? Resolve(IConfigStore store)
    {
        var settings = store.Load();
        if (!settings.Telemetry.CollectAnonymousData)
        {
            if (!string.IsNullOrEmpty(settings.Telemetry.InstallId))
                store.Update(s => s.Telemetry.InstallId = "");
            return null;
        }

        var id = settings.Telemetry.InstallId;
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            store.Update(s => s.Telemetry.InstallId = id);
        }
        return id;
    }
}
