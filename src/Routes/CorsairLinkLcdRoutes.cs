using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapCorsairLcdEndpoints(WebApplication app)
    {
        app.MapPost("/devices/corsair/lcd/media/upload", async (
            HttpContext ctx,
            CorsairLinkLcdMediaLibrary library) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new LcdUploadResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.LcdUploadResponse,
                    statusCode: 400);
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    new LcdUploadResponse { Error = true, Msg = "No file provided" },
                    AppJsonContext.Default.LcdUploadResponse,
                    statusCode: 400);
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.Json(
                    new LcdUploadResponse
                    {
                        Error = true,
                        Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)",
                    },
                    AppJsonContext.Default.LcdUploadResponse,
                    statusCode: 400);
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nexus-lcd-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
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
                        new LcdUploadResponse { Error = true, Msg = result.Error ?? "Import failed" },
                        AppJsonContext.Default.LcdUploadResponse,
                        statusCode: 400);
                }

                return Results.Json(
                    new LcdUploadResponse { Item = result.Item },
                    AppJsonContext.Default.LcdUploadResponse);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }).DisableAntiforgery();

        app.MapGet("/devices/corsair/lcd/media", (CorsairLinkLcdMediaLibrary library) =>
            Results.Json(
                new LcdMediaListResponse { Items = library.ListItems().ToArray() },
                AppJsonContext.Default.LcdMediaListResponse));

        app.MapDelete("/devices/corsair/lcd/media/{id}", (string id, CorsairLinkLcdMediaLibrary library) =>
        {
            if (!CorsairLinkLcdMediaLibrary.IsValidId(id))
            {
                return Results.Json(
                    ApiResponse.Fail("invalid id"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 400);
            }
            library.DeleteItem(id);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPut("/devices/corsair/lcd/settings", (
            LcdSettingsRequest body,
            CorsairLinkLcd lcd,
            IConfigStore store) =>
        {
            if (body.Brightness.HasValue) lcd.SetBrightness(body.Brightness.Value);
            if (body.Rotation.HasValue) lcd.SetRotation(body.Rotation.Value);

            store.Update(s =>
            {
                if (body.MediaId is not null)
                {
                    s.Devices.Corsair.LcdSelectedMediaId = body.MediaId;
                }
                if (body.Brightness.HasValue)
                {
                    s.Devices.Corsair.LcdBrightness = body.Brightness.Value;
                }
                if (body.Rotation.HasValue)
                {
                    s.Devices.Corsair.LcdRotation = body.Rotation.Value;
                }
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/corsair/lcd/state", (CorsairLinkLcd lcd, IConfigStore store) =>
        {
            var s = store.Load().Devices.Corsair;
            return Results.Json(
                new LcdStateResponse
                {
                    HasDevice = lcd.HasDevice,
                    SelectedMediaId = s.LcdSelectedMediaId,
                    Brightness = s.LcdBrightness,
                    Rotation = s.LcdRotation,
                },
                AppJsonContext.Default.LcdStateResponse);
        });
    }
}

public sealed class LcdMediaListResponse
{
    public LcdMediaItem[] Items { get; set; } = Array.Empty<LcdMediaItem>();
}

public sealed class LcdUploadResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public LcdMediaItem? Item { get; set; }
}

public sealed class LcdSettingsRequest
{
    public string? MediaId { get; set; }
    public byte? Brightness { get; set; }
    public byte? Rotation { get; set; }
}

public sealed class LcdStateResponse
{
    public bool HasDevice { get; set; }
    public string? SelectedMediaId { get; set; }
    public byte Brightness { get; set; }
    public byte Rotation { get; set; }
}
