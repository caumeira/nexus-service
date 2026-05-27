using Nexus.Service.Devices;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapDeviceSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/devices/cnvs", (IDeviceProvider d) => d.GetCnvs());
        app.MapPost("/devices/cnvs", (SetCnvsSettingsBody body, IDeviceProvider d) => d.SetCnvs(body));
    }
}
