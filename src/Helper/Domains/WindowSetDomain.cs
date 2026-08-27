#if WINDOWS
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>windowSet.snapshot</c>. Helper-to-service, one-way: the
/// set of process ids currently owning a visible top-level window with no
/// owner, refreshed on WindowSetPoller's timer. Session 0 (the LocalSystem
/// service) cannot enumerate these directly - see WindowsWindowSetProvider.
/// </summary>
public sealed class WindowSetSnapshotPayload
{
    public List<int> Pids { get; set; } = new();
}

/// <summary>
/// Payload for <c>windowSet.wanted</c>. Service-to-helper. Both consumers of
/// the snapshot (the processes frame's App/Background split and
/// MonitoringEventCollector's app open/close events) stop when the Monitoring
/// feature gate is off, but the enumeration that feeds them is in another
/// process and used to keep running regardless - a user turning Monitoring off
/// to stop the work saw nothing change.
/// </summary>
public sealed class WindowSetWantedPayload
{
    public bool Wanted { get; set; }
}

public static class WindowSetCommands
{
    public const string SnapshotType = "windowSet.snapshot";
    public const string WantedType = "windowSet.wanted";

    public static Task SetWantedAsync(HelperRegistry registry, bool wanted, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: WantedType,
            payload: new WindowSetWantedPayload { Wanted = wanted },
            payloadType: AppJsonContext.Default.WindowSetWantedPayload,
            ct: ct);
    }
}

/// <summary>Helper side of <c>windowSet.wanted</c>; see WindowSetPoller.SetWanted.</summary>
public sealed class WindowSetHandler
{
    private readonly Action<bool> _setWanted;

    public WindowSetHandler(Action<bool> setWanted)
    {
        _setWanted = setWanted;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(WindowSetCommands.WantedType, (env, _) =>
        {
            if (env.Payload is null) return Task.FromResult(env.Ok());
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.WindowSetWantedPayload);
            if (p is not null) _setWanted(p.Wanted);
            return Task.FromResult(env.Ok());
        });
    }
}

// The snapshot direction stays one-way push, same shape as screenTime: the
// helper's WindowSetPoller pushes via HelperOutbound directly, consumed by
// WindowsWindowSetProvider's inbound envelope handler.
#endif
