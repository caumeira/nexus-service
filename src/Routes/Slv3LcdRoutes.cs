using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class Slv3LcdScreenDto
{
    public string Serial { get; set; } = "";
    /// <summary>GroupIndex from GetPosIndex(201); -1 if not yet known.</summary>
    public int Position { get; set; } = -1;
    public int Width { get; set; } = Slv3LcdProtocol.PanelWidth;
    public int Height { get; set; } = Slv3LcdProtocol.PanelHeight;
    public byte Brightness { get; set; }
    public byte Rotation { get; set; }
    public string ContentType { get; set; } = "off";
    public string? MediaId { get; set; }
}

public sealed class Slv3LcdScreensResponse
{
    public List<Slv3LcdScreenDto> Screens { get; set; } = new();
}

public sealed class Slv3LcdSettingsRequest
{
    public string Serial { get; set; } = "";
    public byte? Brightness { get; set; }
    public byte? Rotation { get; set; }
}

public sealed class Slv3LcdContentRequest
{
    public string Serial { get; set; } = "";
    /// <summary>"off" | "image" | "gif" | "video" | "sensor" | "clock".</summary>
    public string ContentType { get; set; } = "";
    public string? MediaId { get; set; }
}

public sealed class Slv3LcdMediaDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"image" | "gif" | "video".</summary>
    public string Kind { get; set; } = "";
}

public sealed class Slv3LcdMediaListResponse
{
    public List<Slv3LcdMediaDto> Items { get; set; } = new();
}

public sealed class Slv3LcdImportResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public string? MediaId { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
}

/// <summary>
/// First-party Lian Li SL-LCD Wireless fan-screen routes: discovery/settings
/// list, per-screen brightness/rotation/content, and the media library
/// (import/list/delete). See plans/lianli-wireless-support.md section 4-5.
/// </summary>
public static class Slv3LcdRoutes
{
    private static readonly string[] ValidContentTypes = { "off", "image", "gif", "video", "sensor", "clock" };

    public static void MapSlv3LcdEndpoints(this WebApplication app)
    {
        app.MapGet("/devices/lianli-wireless/screens", (Slv3LcdHub hub, IConfigStore store) =>
        {
            var screenSettings = store.Load().Devices.LianLiWireless.Screens;
            var response = new Slv3LcdScreensResponse();
            foreach (var screen in hub.Screens)
            {
                var settings = screenSettings.TryGetValue(screen.Serial, out var s)
                    ? s
                    : new LianLiWirelessScreenSettings();
                response.Screens.Add(new Slv3LcdScreenDto
                {
                    Serial = screen.Serial,
                    Position = screen.Position,
                    Brightness = settings.Brightness,
                    Rotation = settings.Rotation,
                    ContentType = settings.ContentType,
                    MediaId = settings.MediaId,
                });
            }
            return Results.Json(response, AppJsonContext.Default.Slv3LcdScreensResponse);
        });

        app.MapPost("/devices/lianli-wireless/screen/settings", (Slv3LcdSettingsRequest body, Slv3LcdHub hub, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Brightness is > 100)
            {
                return Results.Json(ApiResponse.Fail("brightness must be 0-100"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Rotation is > 3)
            {
                return Results.Json(ApiResponse.Fail("rotation must be 0-3"), AppJsonContext.Default.ApiResponse);
            }

            if (body.Brightness.HasValue) hub.SetBrightness(body.Serial, body.Brightness.Value);
            if (body.Rotation.HasValue) hub.SetRotation(body.Serial, body.Rotation.Value);

            store.Update(s =>
            {
                var screen = s.Devices.LianLiWireless.Screens.TryGetValue(body.Serial, out var existing)
                    ? existing
                    : new LianLiWirelessScreenSettings();
                if (body.Brightness.HasValue) screen.Brightness = body.Brightness.Value;
                if (body.Rotation.HasValue) screen.Rotation = body.Rotation.Value;
                s.Devices.LianLiWireless.Screens[body.Serial] = screen;
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-wireless/screen/content", (Slv3LcdContentRequest body, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            if (Array.IndexOf(ValidContentTypes, body.ContentType) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid contentType"), AppJsonContext.Default.ApiResponse);
            }
            if (body.MediaId is not null && !MediaLibrary.IsValidId(body.MediaId))
            {
                return Results.Json(ApiResponse.Fail("invalid mediaId"), AppJsonContext.Default.ApiResponse);
            }

            store.Update(s =>
            {
                var screen = s.Devices.LianLiWireless.Screens.TryGetValue(body.Serial, out var existing)
                    ? existing
                    : new LianLiWirelessScreenSettings();
                screen.ContentType = body.ContentType;
                screen.MediaId = body.ContentType == "off" ? null : body.MediaId;
                s.Devices.LianLiWireless.Screens[body.Serial] = screen;
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-wireless/media/import", async (HttpContext ctx, Slv3LcdMediaLibrary library) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new Slv3LcdImportResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.Slv3LcdImportResponse,
                    statusCode: 400);
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    new Slv3LcdImportResponse { Error = true, Msg = "No file provided" },
                    AppJsonContext.Default.Slv3LcdImportResponse,
                    statusCode: 400);
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.Json(
                    new Slv3LcdImportResponse
                    {
                        Error = true,
                        Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)",
                    },
                    AppJsonContext.Default.Slv3LcdImportResponse,
                    statusCode: 400);
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nexus-lianli-lcd-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                var result = await library.ImportAsync(tempPath, file.FileName);
                if (!result.Ok)
                {
                    return Results.Json(
                        new Slv3LcdImportResponse { Error = true, Msg = result.Error ?? "Import failed" },
                        AppJsonContext.Default.Slv3LcdImportResponse,
                        statusCode: 400);
                }

                return Results.Json(
                    new Slv3LcdImportResponse { MediaId = result.Item!.Id, Name = result.Item.Name, Kind = result.Item.Kind },
                    AppJsonContext.Default.Slv3LcdImportResponse);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }).DisableAntiforgery();

        app.MapGet("/devices/lianli-wireless/media", (Slv3LcdMediaLibrary library) =>
        {
            var response = new Slv3LcdMediaListResponse();
            foreach (var item in library.ListItems())
            {
                response.Items.Add(new Slv3LcdMediaDto { Id = item.Id, Name = item.Name, Kind = item.Kind });
            }
            return Results.Json(response, AppJsonContext.Default.Slv3LcdMediaListResponse);
        });

        app.MapDelete("/devices/lianli-wireless/media/{id}", (string id, Slv3LcdMediaLibrary library) =>
        {
            if (!MediaLibrary.IsValidId(id))
            {
                return Results.Json(ApiResponse.Fail("invalid id"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            library.DeleteItem(id);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}
