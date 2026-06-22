#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>diagnostics.openLogs</c>. Service-to-helper, one-way. The
/// service handler runs as LocalSystem in Session 0, where an explorer.exe it
/// spawns lands in the non-interactive session and never appears. The helper
/// runs in the user session, so it opens the logs folder on the user's
/// desktop. No fields - the envelope's existence is the signal.
/// </summary>
public sealed class OpenLogsPayload { }

// JSON source-gen registration is centralised in
// src/Serialization/AppJsonContext.cs - append a matching
// [JsonSerializable(typeof(OpenLogsPayload))] line there.

/// <summary>
/// Service-side outbound facade. Service code calls this to ask the
/// user-session helper to reveal the logs folder.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiagnosticsCommands
{
    public static Task OpenLogsAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "diagnostics.openLogs",
            payload: new OpenLogsPayload(),
            payloadType: AppJsonContext.Default.OpenLogsPayload,
            ct: ct);
    }
}

/// <summary>
/// Helper-side handler. The helper bootstrap constructs this with the open
/// action and calls <see cref="Register"/> to bind it to the dispatch
/// registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiagnosticsHandler
{
    private readonly Action _onOpenLogs;

    public DiagnosticsHandler(Action onOpenLogs)
    {
        _onOpenLogs = onOpenLogs;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("diagnostics.openLogs", (env, _) =>
        {
            try { _onOpenLogs(); } catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
