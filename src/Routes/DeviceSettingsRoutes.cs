using Qos.Service.Devices;
using Qos.Service.Models.Devices;

namespace Qos.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapDeviceSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/devices/{type}/connected", (string type, IDeviceProvider d) =>
            new IsDeviceConnectedResponse { Connected = d.IsTypeConnected(type) });
        app.MapGet("/devices/fw/{type}/version", (string type, IDeviceProvider d) =>
            new FirmwareVersionResponse { Version = d.GetFirmwareVersion(type) });
        app.MapGet("/devices/cnvs", (IDeviceProvider d) => d.GetCnvs());
        app.MapPost("/devices/cnvs", (SetCnvsSettingsBody body, IDeviceProvider d) => d.SetCnvs(body));
        app.MapPost("/devices/can-update", (UpdateBody body, IDeviceProvider d) =>
            new CheckForUpdateResponse { IsUpdateAvailable = d.CheckForUpdate(body.Id) });
        app.MapPost("/devices/update", (UpdateBody body, IDeviceProvider d) =>
        {
            var (ok, msg) = d.Update(body.Id);
            return new UpdateResponse { Success = ok, Message = msg };
        });
        app.MapPost("/devices/update-progress", (UpdateBody body, IDeviceProvider d) =>
        {
            var (ok, msg) = d.UpdateProgress(body.Id);
            return new UpdateResponse { Success = ok, Message = msg };
        });
        app.MapGet("/devices/motherboard/leds", (IDeviceProvider d) =>
            new GetMotherboardLEDsResponse { Channels = new(d.GetMotherboardLeds()) });
        app.MapPost("/devices/motherboard/leds", (SetMotherboardLEDsBody body, IDeviceProvider d) =>
            new GetMotherboardLEDsResponse { Channels = new(d.SetMotherboardLeds(body.Channels)) });
        app.MapPost("/devices/function-check", (CheckFirmwareFunctionBody body, IDeviceProvider d) =>
            new CheckFirmwareFunctionResponse { IsFunctionAvailable = d.CheckFirmwareFunction(body) });
    }
}
