#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>monitor.enumerate</c>. Service-to-helper RPC. Empty -
/// the helper enumerates every attached display via DXGI in its user
/// session and returns the list. Session 0 (LocalSystem service)
/// cannot see DXGI outputs at all.
/// </summary>
public sealed class MonitorEnumerateRequest { }

/// <summary>Response for <c>monitor.enumerate</c>. Empty list = no displays detected (or no helper connected).</summary>
public sealed class MonitorListResult
{
    public List<ScreenSyncMonitor> Monitors { get; set; } = new();
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class MonitorCommands
{
    public static async Task<List<ScreenSyncMonitor>> EnumerateAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return new List<ScreenSyncMonitor>();
        var res = await conn.SendCommandAsync(
            type: "monitor.enumerate",
            payload: new MonitorEnumerateRequest(),
            payloadType: AppJsonContext.Default.MonitorEnumerateRequest,
            timeoutMs: 4000,
            ct: ct).ConfigureAwait(false);
        if (!res.Ok || res.Payload is null) return new List<ScreenSyncMonitor>();
        try
        {
            var dto = JsonSerializer.Deserialize(res.Payload.Value, AppJsonContext.Default.MonitorListResult);
            return dto?.Monitors ?? new List<ScreenSyncMonitor>();
        }
        catch { return new List<ScreenSyncMonitor>(); }
    }
}

/// <summary>
/// Helper-side handler. <c>MonitorEnumerator.List()</c> is a stateless
/// static so no dependencies are needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MonitorsHandler
{
    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("monitor.enumerate", (env, _) =>
        {
            var monitors = Nexus.Service.Platform.MonitorEnumerator.List();
            return Task.FromResult(new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(
                    new MonitorListResult { Monitors = monitors },
                    AppJsonContext.Default.MonitorListResult),
            });
        });
    }
}
#endif
