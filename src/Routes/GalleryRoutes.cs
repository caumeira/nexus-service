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
/// management and filesystem browsing are desktop-tier only — browse in
/// particular exposes the host filesystem and must never be reachable from
/// a paired panel session.
/// </summary>
public static class GalleryRoutes
{
    private const int MaxBrowseEntries = 1000;

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

        app.MapGet("/gallery/browse", (string? path) => Browse(path));

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

    private static GalleryBrowseResponse Browse(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return BrowseRoots();
        }

        if (!Path.IsPathFullyQualified(requested))
        {
            return BrowseFail("path must be absolute");
        }

        string full;
        try
        {
            full = Path.GetFullPath(requested);
        }
        catch
        {
            return BrowseFail("invalid path");
        }

        if (!Directory.Exists(full))
        {
            return BrowseFail("folder not found");
        }

        var response = new GalleryBrowseResponse
        {
            Path = full,
            Parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)),
        };

        try
        {
            var dirs = new List<GalleryBrowseEntry>();
            foreach (var dir in Directory.GetDirectories(full))
            {
                if (IsHidden(dir))
                {
                    continue;
                }

                dirs.Add(new GalleryBrowseEntry { Name = Path.GetFileName(dir), Path = dir });
                if (dirs.Count >= MaxBrowseEntries)
                {
                    break;
                }
            }

            dirs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            response.Dirs = dirs;

            var files = new List<GalleryBrowseEntry>();
            foreach (var file in Directory.GetFiles(full))
            {
                if (!GalleryLibrary.IsImageFile(file) || IsHidden(file))
                {
                    continue;
                }

                files.Add(new GalleryBrowseEntry { Name = Path.GetFileName(file), Path = file });
                if (files.Count >= MaxBrowseEntries - dirs.Count)
                {
                    break;
                }
            }

            files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            response.Files = files;
        }
        catch (UnauthorizedAccessException)
        {
            return BrowseFail("access denied");
        }
        catch (Exception ex)
        {
            return BrowseFail(ex.Message);
        }

        return response;
    }

    private static GalleryBrowseResponse BrowseRoots()
    {
        var response = new GalleryBrowseResponse { Path = "" };

        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                response.Dirs.Add(new GalleryBrowseEntry
                {
                    Name = drive.Name.TrimEnd(Path.DirectorySeparatorChar),
                    Path = drive.RootDirectory.FullName,
                });
            }

            return response;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
        {
            response.Dirs.Add(new GalleryBrowseEntry { Name = Path.GetFileName(home), Path = home });
        }

        var mountRoots = OperatingSystem.IsMacOS()
            ? new[] { "/Volumes" }
            : new[] { "/media", "/mnt" };
        foreach (var root in mountRoots)
        {
            if (Directory.Exists(root))
            {
                response.Dirs.Add(new GalleryBrowseEntry { Name = root.TrimStart('/'), Path = root });
            }
        }

        return response;
    }

    private static bool IsHidden(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.'))
        {
            return true;
        }

        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static GalleryBrowseResponse BrowseFail(string msg) =>
        new() { Error = true, Msg = msg };

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
