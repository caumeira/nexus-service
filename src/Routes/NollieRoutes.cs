using System;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Nollie;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapNollieEndpoints(WebApplication app)
    {
        // GET /devices/nollie - every attached controller with its standalone lighting.
        app.MapGet("/devices/nollie", (NollieHub hub, IConfigStore store) =>
        {
            var settings = store.Load();
            var controllers = hub.Controllers;
            var boards = new NollieBoardDto[controllers.Count];
            for (var i = 0; i < boards.Length; i++)
            {
                var c = controllers[i];
                var standalone = NollieStandalone.For(settings, c.DeviceId);
                boards[i] = new NollieBoardDto
                {
                    Id = c.DeviceId,
                    Name = c.Spec.Name,
                    Serial = c.Serial,
                    Channels = c.Spec.Channels,
                    Ports = c.Spec.Ports.Count,
                    SupportsStandalone = NollieProtocol.SupportsStandalone(c.Spec),
                    SupportsBuiltInEffect = NollieProtocol.SupportsBuiltInEffect(c.Spec),
                    StandaloneMode = standalone.Mode,
                    StandaloneColor = standalone.Color,
                };
            }
            return Results.Json(new NollieBoardsResponse { Boards = boards }, AppJsonContext.Default.NollieBoardsResponse);
        });

        // PUT /devices/nollie/{id}/standalone - what the board runs once Nexus lets go.
        app.MapPut("/devices/nollie/{id}/standalone", (string id, NollieStandaloneRequest body, NollieHub hub, IConfigStore store) =>
        {
            var controller = hub.Find(id);
            if (controller is null)
                return Results.Json(ApiResponse.Fail("unknown board"), AppJsonContext.Default.ApiResponse);
            if (!NollieProtocol.SupportsStandalone(controller.Spec))
                return Results.Json(ApiResponse.Fail("this board has no standalone lighting"), AppJsonContext.Default.ApiResponse);
            var knownMode = body.Mode is null
                || body.Mode == NollieStandaloneSettings.ModeStatic
                || body.Mode == NollieStandaloneSettings.ModeBuiltIn;
            if (!knownMode)
                return Results.Json(ApiResponse.Fail("unknown mode"), AppJsonContext.Default.ApiResponse);
            if (body.Mode == NollieStandaloneSettings.ModeBuiltIn && !NollieProtocol.SupportsBuiltInEffect(controller.Spec))
                return Results.Json(ApiResponse.Fail("this board has no built-in effect"), AppJsonContext.Default.ApiResponse);
            if (body.Color is not null && !NollieStandalone.TryParseHexColor(body.Color, out _, out _, out _))
                return Results.Json(ApiResponse.Fail("color must be #RRGGBB"), AppJsonContext.Default.ApiResponse);

            store.Update(s =>
            {
                if (!s.Devices.Nollie.Standalone.TryGetValue(id, out var standalone))
                {
                    standalone = new NollieStandaloneSettings();
                    s.Devices.Nollie.Standalone[id] = standalone;
                }
                if (body.Mode is not null) standalone.Mode = body.Mode;
                if (body.Color is not null) standalone.Color = NormalizeHex(body.Color);
            });
            NollieStandalone.Refresh(controller, store.Load());
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static string NormalizeHex(string color)
        => (color.StartsWith('#') ? color : "#" + color).ToUpperInvariant();
}

public sealed class NollieBoardDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Serial { get; set; } = "";
    public int Channels { get; set; }
    public int Ports { get; set; }
    public bool SupportsStandalone { get; set; }
    public bool SupportsBuiltInEffect { get; set; }
    public string StandaloneMode { get; set; } = "";
    public string StandaloneColor { get; set; } = "";
}

public sealed class NollieBoardsResponse
{
    public NollieBoardDto[] Boards { get; set; } = Array.Empty<NollieBoardDto>();
}

public sealed class NollieStandaloneRequest
{
    public string? Mode { get; set; }
    public string? Color { get; set; }
}
