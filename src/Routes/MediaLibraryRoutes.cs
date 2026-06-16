using System.IO;
using Nexus.Service.Auth;
using Nexus.Service.Lighting;
using Nexus.Service.Media;
using Nexus.Service.Models.Media;

namespace Nexus.Service.Routes;

public static class MediaLibraryRoutes
{
    public static void MapMediaLibraryEndpoints(this WebApplication app)
    {
        app.MapGet("/media/library", (MediaLibrary lib) =>
            new MediaLibraryResponse { Items = lib.ListItems() }).AllowPanel();

        app.MapPost("/media/import", async (HttpContext ctx, MediaLibrary lib) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "Expected multipart/form-data" });
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = "No file provided" });
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.BadRequest(new MediaImportResponse { Error = true, Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)" });
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"nexus-import-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                var result = await MediaImporter.ImportAsync(lib, tempPath, file.FileName, form["crop"].ToString());
                if (!result.Ok)
                {
                    return Results.BadRequest(new MediaImportResponse { Error = true, Msg = result.Error ?? "Import failed" });
                }

                return Results.Ok(new MediaImportResponse { Item = result.Item });
            }
            finally
            {
                try
                { File.Delete(tempPath); }
                catch { }
            }
        }).DisableAntiforgery();

        app.MapDelete("/media/{id}", (string id, MediaLibrary lib) =>
        {
            if (!MediaLibrary.IsValidId(id))
                return Results.BadRequest(new MediaPlayResponse { Error = true, Msg = "invalid media id" });

            var deleted = lib.DeleteItem(id);
            return deleted
                ? Results.Ok(new MediaPlayResponse())
                : Results.NotFound();
        });

        app.MapPost("/media/library/open", async (MediaLibrary lib, IServiceProvider sp) =>
        {
            try
            {
                var dir = lib.RootDir;
                Directory.CreateDirectory(dir);
#if WINDOWS
                if (OperatingSystem.IsWindows())
                {
                    // The Session-0 service can't show Explorer; the user-session
                    // helper opens the folder and brings it over the app window.
                    var registry = sp.GetService<Nexus.Service.Helper.HelperRegistry>();
                    if (registry is null ||
                        !await Nexus.Service.Helper.Domains.FileDialogCommands.OpenFolderAsync(registry, dir))
                    {
                        return Results.Problem("no interactive user session");
                    }

                    return Results.Ok(new MediaPlayResponse());
                }
#endif
                await Task.CompletedTask;
                var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false };
                if (OperatingSystem.IsMacOS())
                { psi.FileName = "open"; psi.Arguments = $"\"{dir}\""; }
                else
                { psi.FileName = "xdg-open"; psi.Arguments = $"\"{dir}\""; }
                System.Diagnostics.Process.Start(psi);
                return Results.Ok(new MediaPlayResponse());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[media-library] open folder failed: {ex.Message}");
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/media/{id}/thumbnail", (string id, MediaLibrary lib) =>
        {
            if (!MediaLibrary.IsValidId(id))
                return Results.BadRequest("invalid media id");

            var thumbPath = lib.GetThumbPath(id);
            if (!File.Exists(thumbPath))
            {
                return Results.NotFound();
            }

            return Results.File(thumbPath, "image/jpeg");
        }).AllowPanel();

        app.MapPost("/media/{id}/play", (string id, ILightingProvider lighting) =>
        {
            if (!MediaLibrary.IsValidId(id))
                return Results.BadRequest(new MediaPlayResponse { Error = true, Msg = "invalid media id" });

            var ok = lighting.StartMedia(id);
            return ok
                ? Results.Ok(new MediaPlayResponse())
                : Results.NotFound();
        }).AllowPanel();

        app.MapGet("/media/current", (Nexus.Service.Persistence.IConfigStore store, MediaLibrary lib) =>
        {
            var lastId = store.Load().Lighting.LastMediaId;
            return new MediaCurrentResponse
            {
                MediaId = lastId,
                Item = string.IsNullOrEmpty(lastId) ? null : lib.GetItem(lastId),
            };
        }).AllowPanel();
    }
}
