using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>Read-only cooling host action; apps get no cooling writes.</summary>
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
    }

    public static IReadOnlyList<string> AllActions => new[] { "cooling.state" };
}
