using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Resolves the anonymous per-install id shared by every telemetry channel
/// (fleet heartbeat + product events + fleet events) and enforces the single
/// opt-out. CollectAnonymousData off returns null (nothing is sent), but the
/// stored id is kept so a later opt-in resumes the same id instead of
/// double-counting installs. It is cleared only by a purge uninstall,
/// outside this class.
/// </summary>
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

    /// <returns>The persisted install id regardless of consent, or null when
    /// none has ever been minted. Never mints one itself. The one sanctioned
    /// caller is the opt-out fleet event, which must carry the same id the
    /// install already reported under even though <see cref="Resolve"/> is
    /// already gated closed by the time opt-out fires.</returns>
    public static string? ResolveStored(IConfigStore store)
    {
        var id = store.Load().Telemetry.InstallId;
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
