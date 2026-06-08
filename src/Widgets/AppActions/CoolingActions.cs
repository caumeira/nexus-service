using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using static Nexus.Service.Widgets.AppActions.AppActionHelpers;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Host actions for cooling: read fan channels + temperature
/// sources, set a manual fan duty, apply a built-in profile. Gated through
/// the manifest's capabilities.dispatch allowlist.</summary>
public static class CoolingActions
{
    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("cooling.state", (services, _, _) =>
        {
            var f = services.GetRequiredService<IFanControlProvider>();
            var dto = new AppCoolingStateDto
            {
                Channels = new List<FanChannel>(f.GetFanChannels()),
                Sources = new List<TemperatureSource>(f.GetTemperatureSources()),
            };
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.AppCoolingStateDto);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("cooling.setDuty", (services, args, _) =>
        {
            var id = Str(args, "channelId") ?? Str(args, "id");
            if (string.IsNullOrEmpty(id))
                return Task.FromResult<JsonElement?>(Ack(false, "missing channelId"));
            var v = Num(args, "value");
            if (v is null || v < 0 || v > 100)
                return Task.FromResult<JsonElement?>(Ack(false, "value must be 0..100"));
            var f = services.GetRequiredService<IFanControlProvider>();
            var applied = f.SetFanSpeed(id!, (int)v.Value);
            return Task.FromResult<JsonElement?>(Ack(true, applied: applied.ToString()));
        });

        registry.Register("cooling.applyPreset", (services, args, _) =>
        {
            var name = Str(args, "name") ?? Str(args, "preset");
            if (string.IsNullOrEmpty(name))
                return Task.FromResult<JsonElement?>(Ack(false, "missing name"));
            var f = services.GetRequiredService<IFanControlProvider>();
            var store = services.GetRequiredService<IConfigStore>();
            var applied = FanProfiles.Apply(name!, f, store);
            return Task.FromResult<JsonElement?>(Ack(true, applied: applied));
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "cooling.state", "cooling.setDuty", "cooling.applyPreset" };
}
