#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>Payload for <c>displays.topology</c>. Service-to-helper RPC, empty.</summary>
public sealed class DisplayTopologyRequest { }

/// <summary>Response for <c>displays.topology</c>.</summary>
public sealed class DisplayTopologyResult
{
    public List<RawDisplayInfo> Displays { get; set; } = new();
}

/// <summary>
/// One-way helper-to-service push: the user session received
/// WM_DISPLAYCHANGE (monitor plug/unplug, resolution or arrangement
/// change). Carries no data; the service refetches topology on demand.
/// </summary>
public sealed class DisplaysChangedPayload { }

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.

[SupportedOSPlatform("windows")]
public static class DisplayTopologyCommands
{
    public const string TopologyType = "displays.topology";
    public const string ChangedType = "displays.changed";

    /// <summary>Null = no helper connected (topology unknown), not "no monitors".</summary>
    public static async Task<List<RawDisplayInfo>?> EnumerateAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return null;
        var res = await conn.SendCommandAsync(
            type: TopologyType,
            payload: new DisplayTopologyRequest(),
            payloadType: AppJsonContext.Default.DisplayTopologyRequest,
            timeoutMs: 4000,
            ct: ct).ConfigureAwait(false);
        if (!res.Ok || res.Payload is null) return null;
        try
        {
            var dto = JsonSerializer.Deserialize(res.Payload.Value, AppJsonContext.Default.DisplayTopologyResult);
            return dto?.Displays ?? new List<RawDisplayInfo>();
        }
        catch { return null; }
    }
}

/// <summary>Helper-side handler: runs the real topology provider in the user session.</summary>
[SupportedOSPlatform("windows")]
public sealed class DisplaysHandler
{
    private readonly WindowsDisplayTopologyProvider _provider = new();

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(DisplayTopologyCommands.TopologyType, (env, _) =>
        {
            var displays = _provider.Enumerate() ?? new List<RawDisplayInfo>();
            return Task.FromResult(new HelperResult
            {
                Id = env.Id ?? "",
                Ok = true,
                Payload = JsonSerializer.SerializeToElement(
                    new DisplayTopologyResult { Displays = new List<RawDisplayInfo>(displays) },
                    AppJsonContext.Default.DisplayTopologyResult),
            });
        });
    }
}
#endif
