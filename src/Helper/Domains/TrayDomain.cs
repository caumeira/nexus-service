#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains
{
    /// <summary>
    /// Payload for <c>trayIcon.setVisible</c>. Service-to-helper. The
    /// service pushes the persisted <c>ShowWindowsTrayIcon</c> setting on
    /// every connect so the helper settles to the configured value within
    /// ~1 s of bootstrap.
    /// </summary>
    public sealed class TraySetVisiblePayload
    {
        public bool Visible { get; set; }
    }

    // JSON source-gen registration lives in src/Serialization/AppJsonContext.cs
    // (see note in LifecycleDomain.cs).

    [SupportedOSPlatform("windows")]
    public static class TrayCommands
    {
        public static Task SetVisibleAsync(HelperRegistry registry, bool visible, CancellationToken ct = default)
        {
            var conn = registry.GetAny();
            if (conn is null) return Task.CompletedTask;
            return conn.SendAsync(
                type: "trayIcon.setVisible",
                payload: new TraySetVisiblePayload { Visible = visible },
                payloadType: AppJsonContext.Default.TraySetVisiblePayload,
                ct: ct);
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class TrayHandler
    {
        private readonly Action<bool> _setVisible;

        public TrayHandler(Action<bool> setVisible) { _setVisible = setVisible; }

        public void Register(HelperHandlerRegistry registry)
        {
            registry.Register("trayIcon.setVisible", (env, _) =>
            {
                if (env.Payload is null) return Task.FromResult(env.Ok());
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TraySetVisiblePayload);
                _setVisible(p?.Visible ?? false);
                return Task.FromResult(env.Ok());
            });
        }
    }
}
#endif
