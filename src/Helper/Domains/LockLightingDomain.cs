#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>lighting.lockInputWatch</c>. Service-to-helper, one-way.
/// Arms or disarms the helper's lock-screen input poll; the service sends it
/// as a lock blackout engages and again as it releases, so the poll only runs
/// during the window where its answer can change anything.
/// </summary>
public sealed class LockInputWatchPayload
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Payload for <c>lighting.userInput</c>. Helper-to-service, one-way. Means
/// "someone touched this machine just now" and nothing else - no key, no
/// device, not even keyboard vs mouse. See <see cref="Nexus.Service.Helper.LockInputPoller"/>
/// for why it cannot say more, and why it must not.
/// </summary>
public sealed class LockInputSeenPayload { }

// ---------- Service-side commands (outbound) ----------

/// <summary>Service-side facade for arming the helper's lock-screen input poll.</summary>
[SupportedOSPlatform("windows")]
public static class LockLightingCommands
{
    public const string WatchType = "lighting.lockInputWatch";
    public const string InputSeenType = "lighting.userInput";

    public static Task SetLockInputWatchAsync(HelperRegistry registry, bool enabled, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: WatchType,
            payload: new LockInputWatchPayload { Enabled = enabled },
            payloadType: AppJsonContext.Default.LockInputWatchPayload,
            ct: ct);
    }
}

// ---------- Helper-side handler ----------

/// <summary>Helper-side handler binding <c>lighting.lockInputWatch</c> to the poller's arm switch.</summary>
[SupportedOSPlatform("windows")]
public sealed class LockLightingHandler
{
    private readonly Action<bool> _onWatchChanged;

    public LockLightingHandler(Action<bool> onWatchChanged) => _onWatchChanged = onWatchChanged;

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register(LockLightingCommands.WatchType, (env, _) =>
        {
            try
            {
                var enabled = env.Payload is not null
                    && JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.LockInputWatchPayload)?.Enabled == true;
                _onWatchChanged(enabled);
            }
            catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
