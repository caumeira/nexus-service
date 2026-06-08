using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;
using static Nexus.Service.Widgets.WidgetActions.WidgetActionHelpers;

namespace Nexus.Service.Widgets.WidgetActions;

/// <summary>Host actions for lighting: read the current sync mode, set it
/// (none/animate/music/screen/gif). Gated through the manifest's
/// capabilities.dispatch allowlist.</summary>
public static class LightingActions
{
    public static void RegisterAll(WidgetActionRegistry registry)
    {
        registry.Register("lighting.state", (services, _, _) =>
        {
            var l = services.GetRequiredService<ILightingProvider>();
            var dto = new CurrentSyncResponse { Sync = l.GetSync() };
            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.CurrentSyncResponse);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult<JsonElement?>(doc.RootElement.Clone());
        });

        registry.Register("lighting.setMode", (services, args, _) =>
        {
            var mode = Str(args, "mode") ?? Str(args, "sync");
            if (string.IsNullOrEmpty(mode))
                return Task.FromResult<JsonElement?>(Ack(false, "missing mode"));
            var l = services.GetRequiredService<ILightingProvider>();
            if (mode is "none" or "off") l.StopAll();
            else l.SetSync(mode!);
            return Task.FromResult<JsonElement?>(Ack(true, applied: mode));
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "lighting.state", "lighting.setMode" };
}
