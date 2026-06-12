using Nexus.Service.Models;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;

namespace Nexus.Service.Routes;

public static class KeebRoutes
{
    public static void MapKeebEndpoints(this WebApplication app)
    {
        app.MapGet("/keeb/state", (int? layer, IKeebProvider k) => k.GetState(layer ?? 0));
        app.MapGet("/keeb/layer/{layer}", (int layer, IKeebProvider k) => k.GetState(layer));
        app.MapGet("/keeb/settings", (IKeebProvider k) => k.GetSettings());
        app.MapGet("/keeb/rotary/functions", (IKeebProvider k) =>
            new GetRotaryFunctionsResponse { Functions = k.GetRotaryFunctions() });
        app.MapPost("/keeb/rotary", (SetRotaryWheelsBody body, IKeebProvider k) =>
        {
            k.SetRotary(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/rotary/sensitivity", (SetRotarySensitivityBody body, IKeebProvider k) =>
        {
            k.SetRotarySensitivity(body.Sensitivity);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/firmware/lighting", (SetFirmwareLightingBody body, IKeebProvider k) =>
        {
            k.SetFirmwareLighting(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/passive-lighting", (SetPassiveLightingBody body, IKeebProvider k) =>
        {
            k.SetPassiveLighting(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/game-mode", (SetGameModeBody body, IKeebProvider k) =>
        {
            k.SetGameMode(body);
            return ApiResponse.Ok();
        });
        app.MapGet("/keeb/macro/{index}", (int index, IKeebProvider k) =>
            new GetMacroResponse { Macro = k.GetMacro(index) });
        app.MapPost("/keeb/macro/{index}", (int index, SetMacroBody body, IKeebProvider k) =>
            new GetMacroResponse { Macro = k.SetMacro(index, body) });

        // Diagnostic: the raw 0xF2 layer table as hex, for reverse-engineering
        // the firmware slot order on the bench. Read-only - never writes.
        // Hex rides in ApiResponse.Msg so no new wire type needs registering
        // in the AOT JSON context.
        app.MapGet("/keeb/debug/layer-raw/{layer}", (int layer, Nexus.Service.Peripherals.Hyte.Keeb.KeebHub hub) =>
        {
            var raw = hub.ReadLayerRaw(hub.State.Profile, layer);
            return raw is null
                ? ApiResponse.Fail("layer read unavailable (disconnected or timed out)")
                : ApiResponse.Ok(System.Convert.ToHexString(raw));
        });
    }
}
