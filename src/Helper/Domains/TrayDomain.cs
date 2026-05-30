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

    /// <summary>
    /// Payload for <c>trayIcon.pairNotice</c>. Service-to-helper. The service
    /// raises this when a phone submits a pair request and no dashboard is
    /// open to surface the Allow/Deny modal. <see cref="Show"/> true pops a
    /// tray balloon (click → opens the dashboard); false dismisses it once
    /// the request is resolved.
    /// </summary>
    public sealed class TrayPairNoticePayload
    {
        public bool Show { get; set; }
        public string DeviceLabel { get; set; } = "";
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

        public static Task PairNoticeAsync(HelperRegistry registry, bool show, string deviceLabel, CancellationToken ct = default)
        {
            var conn = registry.GetAny();
            if (conn is null) return Task.CompletedTask;
            return conn.SendAsync(
                type: "trayIcon.pairNotice",
                payload: new TrayPairNoticePayload { Show = show, DeviceLabel = deviceLabel ?? "" },
                payloadType: AppJsonContext.Default.TrayPairNoticePayload,
                ct: ct);
        }
    }

    [SupportedOSPlatform("windows")]
    public sealed class TrayHandler
    {
        private readonly Action<bool> _setVisible;
        private readonly Action<string> _showPairNotice;
        private readonly Action _dismissPairNotice;

        public TrayHandler(Action<bool> setVisible, Action<string> showPairNotice, Action dismissPairNotice)
        {
            _setVisible = setVisible;
            _showPairNotice = showPairNotice;
            _dismissPairNotice = dismissPairNotice;
        }

        public void Register(HelperHandlerRegistry registry)
        {
            registry.Register("trayIcon.setVisible", (env, _) =>
            {
                if (env.Payload is null) return Task.FromResult(env.Ok());
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TraySetVisiblePayload);
                _setVisible(p?.Visible ?? false);
                return Task.FromResult(env.Ok());
            });

            registry.Register("trayIcon.pairNotice", (env, _) =>
            {
                if (env.Payload is null) return Task.FromResult(env.Ok());
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.TrayPairNoticePayload);
                if (p is not null)
                {
                    if (p.Show) _showPairNotice(p.DeviceLabel ?? "");
                    else _dismissPairNotice();
                }
                return Task.FromResult(env.Ok());
            });
        }
    }
}
#endif
