using Nexus.Service.Models;
using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;

namespace Nexus.Service.Routes;

public static class KeebRoutes
{
    public static void MapKeebEndpoints(this WebApplication app)
    {
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
        app.MapPost("/keeb/key-reactive", (SetFirmwareLightingBody body, IKeebProvider k) =>
        {
            k.SetKeyReactive(body);
            return ApiResponse.Ok();
        });
        app.MapPost("/keeb/firmware/lighting", (SetFirmwareLightingBody body, IKeebProvider k) =>
        {
            k.SetFirmwareLighting(body);
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
        app.MapPost("/inputter", (InputterBody body, IInputterProvider i) =>
        {
            i.Send(body);
            return Results.Ok();
        });
    }
}
