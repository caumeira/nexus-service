using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// First-party driver actions: let an allowlisted app's widget start/stop/query its
/// bundled native driver via the external-tool manager. Authorized twice — the
/// dispatch route validates the manifest <c>capabilities.dispatch</c> allowlist, and
/// each handler re-checks the caller against <see cref="FirstPartyDriverApps"/>
/// using the host-injected <c>__callerAppId</c>.
/// </summary>
public static class DriverActions
{
    public static IReadOnlyList<string> AllActions => new[]
    {
        "driver.status", "driver.launch", "driver.terminate",
    };

    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("driver.status", (s, a, ct) => Handle(s, a, ct, Op.Status));
        registry.Register("driver.launch", (s, a, ct) => Handle(s, a, ct, Op.Launch));
        registry.Register("driver.terminate", (s, a, ct) => Handle(s, a, ct, Op.Terminate));
    }

    private enum Op { Status, Launch, Terminate }

    private static async Task<JsonElement?> Handle(
        IServiceProvider services, Dictionary<string, JsonElement>? args, CancellationToken ct, Op op)
    {
        // __callerAppId is injected by the dispatch route from the validated request
        // (host-controlled, overwritten — not a widget-supplied value).
        var appId = AppActionHelpers.Str(args, "__callerAppId");
        if (string.IsNullOrEmpty(appId) || !FirstPartyDriverApps.Contains(appId))
            return Status(ToolStatus.Failed, "not authorized");

        var registry = services.GetRequiredService<AppRegistry>();
        if (!registry.TryGet(appId, out var entry) || entry.Manifest.Driver is null)
            return Status(ToolStatus.Failed, "no driver declared");

        var driver = entry.Manifest.Driver;
        var manager = services.GetRequiredService<ExternalToolManager>();
        var usb = services.GetRequiredService<IUsbEnumerator>();
        var present = DriverToolSpecFactory.DevicePresent(driver, usb);

        switch (op)
        {
            case Op.Terminate:
                manager.Terminate(driver.ToolId);
                return Status(manager.GetStatus(driver.ToolId, present));

            case Op.Launch:
                var spec = DriverToolSpecFactory.ResolvePresent(driver, entry.RootPath, usb);
                if (spec is null) return Status(ToolStatus.NoDevice, "no matching device on the bus");
                await manager.LaunchAsync(spec, ct);
                return Status(manager.GetStatus(driver.ToolId, devicePresent: true));

            default: // Status
                return Status(manager.GetStatus(driver.ToolId, present));
        }
    }

    private static JsonElement Status(ToolStatus status, string? message = null)
    {
        var dto = new DriverStatusDto { Status = ToLegacy(status), Message = message };
        var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.DriverStatusDto);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ToLegacy(ToolStatus status) => status switch
    {
        ToolStatus.Running => "Running",
        ToolStatus.NotRunning => "NotRunning",
        ToolStatus.NoDevice => "NoDevice",
        _ => "Failed",
    };
}
