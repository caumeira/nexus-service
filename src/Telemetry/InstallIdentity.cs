using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Telemetry;

/// <summary>Resolves the shared per-install id; CollectAnonymousData off returns null but keeps the stored id so opt-in resumes it instead of double-counting.</summary>
internal static class InstallIdentity
{
    /// <returns>The anonymous install id, or null when the user has opted out.</returns>
    public static string? Resolve(IConfigStore store)
    {
        var settings = store.Load();
        if (!settings.Telemetry.CollectAnonymousData)
            return null;

        var id = settings.Telemetry.InstallId;
        if (string.IsNullOrEmpty(id))
        {
            id = Guid.NewGuid().ToString("N");
            store.Update(s => s.Telemetry.InstallId = id);
        }
        return id;
    }

    /// <returns>The persisted install id regardless of consent, or null if none was ever minted; never mints one - only the opt-out event needs this after Resolve's gate is closed.</returns>
    public static string? ResolveStored(IConfigStore store)
    {
        var id = store.Load().Telemetry.InstallId;
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
