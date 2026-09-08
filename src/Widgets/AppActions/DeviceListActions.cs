using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Host action exposing the registered device list to declarative widgets.</summary>
public static class DeviceListActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("devices.list", (services, _, _) =>
        {
            var manager = services.GetRequiredService<DeviceManager>();
            var dto = Project(manager.GetAll());

            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.AppDeviceListResponse);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });
    }

    /// <summary>Narrows the internal device list to the fields an app may read; serials, warnings, gate state and bus stay host-side.</summary>
    internal static AppDeviceListResponse Project(IReadOnlyList<DeviceListItem> devices)
    {
        var response = new AppDeviceListResponse();
        foreach (var device in devices)
        {
            response.Devices.Add(new AppDeviceListItem
            {
                Id = device.Id,
                Name = device.Name,
                Category = device.Category,
                Connected = device.Connected,
                FirmwareVersion = device.FirmwareVersion,
            });
        }
        return response;
    }

    public static IReadOnlyList<string> AllActions => new[] { "devices.list" };
}
