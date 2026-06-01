using System.Linq;
using Nexus.Service.Models;
using Nexus.Service.Models.Peripherals;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.Capabilities;

namespace Nexus.Service.Routes;

public static class PeripheralRoutes
{
    public static void MapPeripheralEndpoints(this WebApplication app)
    {
        // List all detected peripherals (live + capability manifest, no deep state).
        app.MapGet("/peripherals", (PeripheralRegistry reg) =>
        {
            var items = reg.GetAll().Select(BuildSummary).ToList();
            return new GetPeripheralsResponse { Items = items };
        });

        // Full peripheral detail with current capability state.
        app.MapGet("/peripherals/{id}", (string id, PeripheralRegistry reg) =>
        {
            var p = reg.Get(id);
            if (p is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(BuildDetail(p));
        });

        // Static catalog — peripherals (mice / keyboards / headsets with config support).
        app.MapGet("/peripherals/supported", () =>
            new GetSupportedDevicesResponse { Items = SupportedDevicesCatalog.All.ToList() });

        // Static catalog — RGB/lighting-capable devices (backed by OpenRGB).
        app.MapGet("/peripherals/lighting-supported", () =>
            new GetSupportedDevicesResponse { Items = LightingDevicesCatalog.All.ToList() });

        // Capability writes — all exceptions are caught so a device protocol glitch
        // returns a 500 with a readable body instead of an unhandled server error.
        app.MapPut("/peripherals/{id}/dpi", (string id, SetDpiBody body, PeripheralRegistry reg) =>
        {
            try
            {
                var p = reg.Get(id);
                var dpi = p?.GetCapability<IDpiCapability>();
                if (dpi is null)
                    return Results.NotFound();
                bool ok = body.Dpi is not null ? dpi.SetDpi(body.Dpi.Value)
                       : body.Stage is not null && dpi.SetActiveStage(body.Stage.Value);
                return ok ? Results.Ok(ApiResponse.Ok()) : Results.Problem("device did not accept the DPI write");
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine($"[peripherals] set-dpi failed: {ex}");
                return Results.Problem(ex.Message);
            }
        });

        app.MapPut("/peripherals/{id}/polling", (string id, SetPollingBody body, PeripheralRegistry reg) =>
        {
            try
            {
                var p = reg.Get(id);
                var poll = p?.GetCapability<IPollingRateCapability>();
                if (poll is null)
                    return Results.NotFound();
                return poll.SetHz(body.Hz) ? Results.Ok(ApiResponse.Ok()) : Results.Problem("device did not accept the polling write");
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine($"[peripherals] set-polling failed: {ex}");
                return Results.Problem(ex.Message);
            }
        });

        app.MapPut("/peripherals/{id}/sleep", (string id, SetSleepBody body, PeripheralRegistry reg) =>
        {
            try
            {
                var p = reg.Get(id);
                var sleep = p?.GetCapability<ISleepCapability>();
                if (sleep is null)
                    return Results.NotFound();
                var ok = true;
                if (body.IdleSeconds is not null)
                    ok &= sleep.SetIdleSeconds(body.IdleSeconds.Value);
                if (body.LowBatteryPercent is not null)
                    ok &= sleep.SetLowBatteryPercent(body.LowBatteryPercent.Value);
                return ok ? Results.Ok(ApiResponse.Ok()) : Results.Problem("device did not accept the sleep write");
            }
            catch (System.Exception ex)
            {
                System.Console.Error.WriteLine($"[peripherals] set-sleep failed: {ex}");
                return Results.Problem(ex.Message);
            }
        });
    }

    private static PeripheralDto BuildSummary(IPeripheral p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Vendor = p.Vendor,
        Category = p.Category,
        VendorId = $"0x{p.VendorId:X4}",
        ProductId = $"0x{p.ProductId:X4}",
        Serial = p.Serial,
        FirmwareVersion = p.FirmwareVersion,
        IsWireless = p.IsWireless,
        Capabilities = p.Capabilities.ToList(),
    };

    private static PeripheralDto BuildDetail(IPeripheral p)
    {
        var dto = BuildSummary(p);

        var dpi = p.GetCapability<IDpiCapability>();
        if (dpi is not null)
        {
            dto.Dpi = new DpiState
            {
                MinDpi = dpi.MinDpi,
                MaxDpi = dpi.MaxDpi,
                Step = dpi.Step,
                StageCount = dpi.StageCount,
                ActiveStage = dpi.ActiveStage,
                StageDpi = dpi.StageDpi.ToList(),
                Current = dpi.GetCurrent(),
            };
        }

        var polling = p.GetCapability<IPollingRateCapability>();
        if (polling is not null)
        {
            dto.Polling = new PollingState
            {
                SupportedHz = polling.SupportedHz.ToList(),
                CurrentHz = polling.GetCurrentHz(),
            };
        }

        var battery = p.GetCapability<IBatteryCapability>();
        if (battery is not null)
        {
            dto.Battery = new BatteryState
            {
                Percent = battery.GetPercent(),
                Charging = battery.IsCharging(),
            };
        }

        var sleep = p.GetCapability<ISleepCapability>();
        if (sleep is not null)
        {
            dto.Sleep = new SleepState
            {
                IdleSeconds = sleep.GetIdleSeconds(),
                LowBatteryPercent = sleep.GetLowBatteryPercent(),
            };
        }

        return dto;
    }
}
