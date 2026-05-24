using Nexus.Service.Devices;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapDeviceListingEndpoints(WebApplication app)
    {
        // Unified device list - registered handlers with connection status
        app.MapGet("/devices/all", (DeviceManager dm) => dm.GetAll());

        // Raw USB device list - every device the OS reports, with full details
        app.MapGet("/devices/usb/all", (DeviceManager dm) => dm.GetUsbDevices());
    }
}
