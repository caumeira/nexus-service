using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Gallery widget backend: per-system shared image sources (referenced
/// files/folders + uploads). Item reads are panel-accessible; source
/// management and the native file-picker dialog are desktop-tier only and
/// must never be reachable from a paired panel session.
/// </summary>
public static class GalleryRoutes
{
    public static void MapGalleryEndpoints(this WebApplication app)
    {
        app.MapGet("/gallery/sources", (GalleryLibrary lib) =>
            new GallerySourcesResponse { Sources = lib.ListSources() });

        app.MapPost("/gallery/sources", (AddGallerySourceBody body, GalleryLibrary lib, MultiplexHub hub) =>
        {
            var result = lib.AddReference(body.Path, body.Kind);
            if (result.Error)
            {
                return Results.BadRequest(result);
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(result);
        });

        app.MapDelete("/gallery/sources/{id}", (string id, GalleryLibrary lib, MultiplexHub hub) =>
        {
            // IsValidId is load-bearing here, not hygiene: the static-asset
            // auth lane is method-blind, so "DELETE /gallery/sources/x.json"
            // reaches this handler unauthenticated — the dot-rejecting id
            // check is what turns it into a 404.
            if (!MediaLibrary.IsValidId(id) || !lib.RemoveSource(id))
            {
                return Results.NotFound();
            }

            PanelTopics.BroadcastGallery(hub);
            return Results.Ok(new GallerySourceMutationResponse());
        });

        app.MapGet("/gallery/items", (GalleryLibrary lib) =>
            new GalleryItemsResponse { Items = lib.EnumerateItems() }).AllowPanel();

        app.MapGet("/gallery/items/{id}/file", (string id, GalleryLibrary lib) =>
        {
            var path = MediaLibrary.IsValidId(id) ? lib.ResolveItemPath(id) : null;
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(path, ContentTypeFor(path));
        }).AllowPanel();

        app.MapGet("/gallery/items/{id}/thumbnail", async (string id, GalleryLibrary lib) =>
        {
            var path = MediaLibrary.IsValidId(id) ? lib.ResolveItemPath(id) : null;
            if (path is null)
            {
                return Results.NotFound();
            }

            var thumb = await GalleryThumbnails.GetOrCreateAsync(lib, id, path);
            return thumb is null ? Results.NotFound() : Results.File(thumb, "image/jpeg");
        }).AllowPanel();

        // Native OS file/folder picker on the host PC. Desktop-tier only and
        // deliberately NOT relayed — the dialog opens on the host's screen.
        // RequestAborted flows in so closing the page abandons the wait (and
        // kills the dialog child process on macOS/Linux).
        app.MapPost("/gallery/pick", async (GalleryPickBody body, IGalleryDialogPicker picker, HttpContext ctx) =>
            await picker.PickAsync(body.Folder, ctx.RequestAborted));

        app.MapPost("/gallery/import", async (HttpContext ctx, GalleryLibrary lib, MultiplexHub hub) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new GallerySourceMutationResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new GallerySourceMutationResponse { Error = true, Msg = "No file provided" });
            }

            if (file.Length > GalleryLibrary.MaxUploadSize)
            {
                return Results.BadRequest(new GallerySourceMutationResponse { Error = true, Msg = $"File too large (max {GalleryLibrary.MaxUploadSize / 1024 / 1024} MB)" });
            }

            if (!GalleryLibrary.IsImageFile(file.FileName))
            {
                return Results.BadRequest(new GallerySourceMutationResponse { Error = true, Msg = "Unsupported image format" });
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"nexus-gallery-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                var result = lib.AddUpload(tempPath, file.FileName);
                if (result.Error)
                {
                    return Results.BadRequest(result);
                }

                PanelTopics.BroadcastGallery(hub);
                return Results.Ok(result);
            }
            finally
            {
                try
                { File.Delete(tempPath); }
                catch { }
            }
        }).DisableAntiforgery();
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            _ => "application/octet-stream",
        };
}
