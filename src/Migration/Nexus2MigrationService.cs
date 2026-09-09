using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;

namespace Nexus.Service.Migration;

/// <summary>Preview (read-only) and apply orchestration for the Nexus 2
/// personalization import. Preview composes Nexus2Translator output into the
/// wire report; Apply drives the live services (panel records, gallery,
/// background media, settings) per selected category.</summary>
public sealed class Nexus2MigrationService
{
    private readonly INexus2ConfigReader _reader;
    private readonly PanelDeviceRegistry _panels;
    private readonly GalleryLibrary _gallery;
    private readonly PanelBgLibrary _bgLibrary;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly IServiceProvider _services;

    public Nexus2MigrationService(
        INexus2ConfigReader reader, PanelDeviceRegistry panels, GalleryLibrary gallery,
        PanelBgLibrary bgLibrary, IConfigStore store, MultiplexHub hub, IServiceProvider services)
    {
        _reader = reader;
        _panels = panels;
        _gallery = gallery;
        _bgLibrary = bgLibrary;
        _store = store;
        _hub = hub;
        _services = services;
    }

    public Nexus2PreviewResponse Preview()
    {
        using var read = _reader.Read();
        if (read is null)
        {
            return new Nexus2PreviewResponse { Available = false };
        }

        var root = read.Document.RootElement;
        var profile = Nexus2Translator.FindActiveProfile(root);
        var y70 = profile is { } p ? GetY70(p) : null;
        var q60Software = profile is { } p2 ? GetQ60Software(p2) : null;

        var response = new Nexus2PreviewResponse
        {
            Available = true,
            ProfileName = profile is { } p3 ? Nexus2Json.GetString(p3, "name") : null,
        };

        response.Categories.Add(BuildAppearanceCategory(y70));
        response.Categories.Add(BuildY70LayoutCategory(y70));
        response.Categories.Add(BuildQ60FaceCategory(q60Software));
        response.Categories.Add(BuildWallpapersCategory(q60Software, read.ConfigDir));
        response.Categories.Add(BuildGallerySourcesCategory(y70));
        response.Categories.Add(BuildRotationCategory(root));
        response.Categories.Add(BuildLanguageCategory(root));
        return response;
    }

    private static Nexus2PreviewCategoryDto BuildAppearanceCategory(JsonElement? y70)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "appearance" };
        if (y70 is not { } y)
        {
            return cat;
        }
        var appearance = Nexus2Y70Translator.TranslateAppearance(y);
        cat.Available = appearance.Available;
        cat.AccentColor = appearance.AccentHex;
        cat.Background = appearance.BackgroundMode == "solid" ? "solid" : appearance.BackgroundEffect;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildY70LayoutCategory(JsonElement? y70)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "y70Layout", DroppedTypes = new List<string>() };
        if (y70 is not { } y)
        {
            return cat;
        }
        var layout = Nexus2Y70Translator.TranslateLayout(y);
        cat.Available = true;
        cat.Pages = layout.Pages;
        cat.Widgets = layout.Widgets;
        cat.MappedWidgets = layout.MappedWidgets;
        cat.DroppedTypes = layout.DroppedTypes;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildQ60FaceCategory(JsonElement? q60Software)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "q60Face" };
        if (q60Software is not { } qs)
        {
            return cat;
        }
        var face = Nexus2Q60Translator.TranslateFace(qs);
        cat.Available = face.Available;
        cat.Face = face.ActiveWidgetType;
        cat.StashedFaces = face.StashedConfigs.Count;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildWallpapersCategory(JsonElement? q60Software, string configDir)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "wallpapers" };
        if (q60Software is not { } qs)
        {
            return cat;
        }
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(qs, configDir, File.Exists);
        cat.Available = wallpaper.Available;
        cat.Count = wallpaper.Available ? 1 : 0;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildGallerySourcesCategory(JsonElement? y70)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "gallerySources" };
        if (y70 is not { } y)
        {
            return cat;
        }
        var sources = Nexus2Y70Translator.TranslateGallerySources(y, File.Exists);
        cat.Available = sources.Available;
        cat.Count = sources.ExistingPaths.Count;
        cat.Missing = sources.Missing;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildRotationCategory(JsonElement root)
    {
        var value = Nexus2Translator.TranslateRotation(root);
        return new Nexus2PreviewCategoryDto { Id = "rotation", Available = value is not null, Value = value };
    }

    private static Nexus2PreviewCategoryDto BuildLanguageCategory(JsonElement root)
    {
        var value = Nexus2Translator.TranslateLanguage(root);
        return new Nexus2PreviewCategoryDto { Id = "language", Available = value is not null, Value = value };
    }

    public async Task<Nexus2ApplyResponse> ApplyAsync(IReadOnlyList<string> categories, bool replaceCustomizedLayout)
    {
        var response = new Nexus2ApplyResponse();
        using var read = _reader.Read();
        if (read is null)
        {
            foreach (var id in categories)
            {
                response.Results.Add(Fail(id, "no-config"));
            }
            return response;
        }

        var root = read.Document.RootElement;
        var profile = Nexus2Translator.FindActiveProfile(root);
        var y70 = profile is { } p ? GetY70(p) : null;
        var q60Software = profile is { } p2 ? GetQ60Software(p2) : null;
        var y70Record = FindSingleInstanceRecord(PanelSurfaces.Y70);
        var q60Record = FindSingleInstanceRecord(PanelSurfaces.Q60);

        var appliedAny = false;
        foreach (var id in categories)
        {
            var result = id switch
            {
                "appearance" => ApplyAppearance(y70, y70Record),
                "y70Layout" => ApplyY70Layout(y70, y70Record, replaceCustomizedLayout),
                "q60Face" => ApplyQ60Face(q60Software, q60Record, replaceCustomizedLayout),
                "wallpapers" => await ApplyWallpaperAsync(q60Software, read.ConfigDir, q60Record),
                "gallerySources" => ApplyGallerySources(y70),
                "rotation" => ApplyRotation(root),
                "language" => ApplyLanguage(root),
                _ => Skip(id, "unknown-category"),
            };
            if (result.Status == "applied")
            {
                appliedAny = true;
            }
            response.Results.Add(result);
        }

        if (appliedAny)
        {
            // Double-check-under-lock, same shape as FanProfiles.SeedDefaultPresetCurves:
            // avoids a redundant write when two apply calls race.
            if (!_store.Load().Nexus2MigrationCompleted)
            {
                _store.Update(s =>
                {
                    if (!s.Nexus2MigrationCompleted)
                    {
                        s.Nexus2MigrationCompleted = true;
                    }
                });
            }
            PanelTopics.BroadcastPrefs(_hub);
            PanelTopics.BroadcastLighting(_hub);
            PanelTopics.BroadcastCooling(_hub);
        }
        return response;
    }

    private Nexus2ApplyResultDto ApplyAppearance(JsonElement? y70, PanelDeviceRecord? y70Record)
    {
        const string id = "appearance";
        if (y70 is not { } y)
        {
            return Fail(id, "no-y70-data");
        }
        if (y70Record is null)
        {
            return Skip(id, "no-y70-record");
        }
        var appearance = Nexus2Y70Translator.TranslateAppearance(y);
        if (!appearance.Available)
        {
            return Skip(id, "no-theme-data");
        }

        var patch = new PanelDevicePatch { BackgroundMode = appearance.BackgroundMode };
        if (appearance.AccentHex is not null)
        {
            patch.AccentColor = appearance.AccentHex;
        }
        if (appearance.BackgroundEffect is not null)
        {
            patch.BackgroundEffect = appearance.BackgroundEffect;
        }
        if (appearance.BackgroundOpacity is not null)
        {
            patch.BackgroundOpacity = appearance.BackgroundOpacity;
        }
        return _panels.Patch(y70Record.Id, patch) is not null ? Applied(id) : Fail(id, "patch-failed");
    }

    private Nexus2ApplyResultDto ApplyY70Layout(JsonElement? y70, PanelDeviceRecord? y70Record, bool replaceCustomizedLayout)
    {
        const string id = "y70Layout";
        if (y70 is not { } y)
        {
            return Fail(id, "no-y70-data");
        }
        if (y70Record is null)
        {
            return Skip(id, "no-y70-record");
        }
        var translated = Nexus2Y70Translator.TranslateLayout(y);
        if (translated.Layout is null)
        {
            return Skip(id, "no-widgets");
        }

        // A null Layout is the only state PanelLayoutDefaults seeds from -
        // any non-null Layout was written by a user edit (or an earlier
        // import), so presence alone is the customization signal.
        if (y70Record.Layout is not null && !replaceCustomizedLayout)
        {
            return new Nexus2ApplyResultDto { Id = id, Status = "needsConfirm", Detail = "layout-customized" };
        }

        return _panels.Patch(y70Record.Id, new PanelDevicePatch { Layout = translated.Layout }) is not null
            ? Applied(id)
            : Fail(id, "patch-failed");
    }

    private Nexus2ApplyResultDto ApplyQ60Face(JsonElement? q60Software, PanelDeviceRecord? q60Record, bool replaceCustomizedLayout)
    {
        const string id = "q60Face";
        if (q60Software is not { } qs)
        {
            return Fail(id, "no-q60-data");
        }
        if (q60Record is null)
        {
            return Skip(id, "no-q60-record");
        }
        var face = Nexus2Q60Translator.TranslateFace(qs);
        if (!face.Available)
        {
            return Skip(id, "no-pages");
        }

        // Same customization signal as ApplyY70Layout: PanelLayoutDefaults
        // never writes its seed back to the record, so a non-null Layout only
        // happens via a real user edit or an earlier import.
        if (q60Record.Layout is not null && !replaceCustomizedLayout)
        {
            return new Nexus2ApplyResultDto { Id = id, Status = "needsConfirm", Detail = "layout-customized" };
        }

        var layout = q60Record.Layout ?? PanelLayoutDefaults.ForSurface(PanelSurfaces.Q60);
        if (face.ActiveWidgetType is not null)
        {
            if (layout.Pages.Count == 0)
            {
                layout.Pages.Add(new PanelPageDto { Id = Guid.NewGuid().ToString() });
            }
            var page = layout.Pages[0];
            var existingId = page.Widgets.Count > 0 ? page.Widgets[0].Id : Guid.NewGuid().ToString();
            var newWidget = new PanelWidgetDto { Id = existingId, Type = face.ActiveWidgetType, Size = "2x4", Config = face.ActiveConfig };
            if (page.Widgets.Count > 0)
            {
                page.Widgets[0] = newWidget;
            }
            else
            {
                page.Widgets.Add(newWidget);
            }
        }
        if (face.StashedConfigs.Count > 0)
        {
            layout.SingleWidgetConfigs ??= new Dictionary<string, Dictionary<string, JsonElement>>();
            foreach (var kv in face.StashedConfigs)
            {
                layout.SingleWidgetConfigs[kv.Key] = kv.Value;
            }
        }

        var patch = new PanelDevicePatch { Layout = layout };
        if (face.AccentHex is not null)
        {
            patch.AccentColor = face.AccentHex;
        }
        return _panels.Patch(q60Record.Id, patch) is not null ? Applied(id) : Fail(id, "patch-failed");
    }

    private async Task<Nexus2ApplyResultDto> ApplyWallpaperAsync(JsonElement? q60Software, string configDir, PanelDeviceRecord? q60Record)
    {
        const string id = "wallpapers";
        if (q60Software is not { } qs)
        {
            return Fail(id, "no-q60-data");
        }
        if (q60Record is null)
        {
            return Skip(id, "no-q60-record");
        }
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(qs, configDir, File.Exists);
        if (!wallpaper.Available || wallpaper.AbsolutePath is null || wallpaper.FileName is null)
        {
            return Skip(id, "no-wallpaper");
        }
        if (FfmpegResolver.Path is null)
        {
            return Fail(id, "ffmpeg-missing");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"nexus2-wallpaper-{Guid.NewGuid():N}{Path.GetExtension(wallpaper.FileName)}");
        try
        {
            // The source file lives under Nexus 2's own directory and must
            // never be moved/deleted; copy before StageAsync, which moves
            // whatever path it is given into its own staging area.
            File.Copy(wallpaper.AbsolutePath, tempPath, overwrite: true);
            var stage = await PanelBgImporter.StageAsync(_bgLibrary, q60Record.Id, tempPath, wallpaper.FileName);
            if (!stage.Ok || stage.StageId is null)
            {
                return Fail(id, "stage-failed");
            }

            var commit = await PanelBgImporter.CommitAsync(_bgLibrary, q60Record.Id, stage.StageId, new CropRect(0, 0, 1, 1), 720, 1280);
            if (!commit.Ok || commit.Item is null)
            {
                return Fail(id, "commit-failed");
            }

            _panels.Patch(q60Record.Id, new PanelDevicePatch
            {
                BackgroundMode = "media",
                BackgroundMediaId = commit.Item.Id,
                BackgroundMediaType = commit.Item.Type,
                BackgroundMediaAlpha = commit.Item.Alpha,
            });
            return Applied(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(id, "copy-failed");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* already moved by StageAsync on success */ }
        }
    }

    private Nexus2ApplyResultDto ApplyGallerySources(JsonElement? y70)
    {
        const string id = "gallerySources";
        if (y70 is not { } y)
        {
            return Fail(id, "no-y70-data");
        }
        var sources = Nexus2Y70Translator.TranslateGallerySources(y, File.Exists);
        if (!sources.Available)
        {
            return Skip(id, "no-gallery-widget");
        }
        if (sources.ExistingPaths.Count == 0)
        {
            return Skip(id, "no-files-found");
        }

        var added = 0;
        foreach (var path in sources.ExistingPaths)
        {
            var result = _gallery.AddReference(path, GallerySourceKinds.File);
            if (result.Source is not null)
            {
                added++;
            }
        }
        return added > 0 ? Applied(id) : Skip(id, "already-added");
    }

    private Nexus2ApplyResultDto ApplyRotation(JsonElement root)
    {
        const string id = "rotation";
        var value = Nexus2Translator.TranslateRotation(root);
        if (value is null)
        {
            return Skip(id, "no-rotation-data");
        }
        _store.Update(s => s.QSeries.Orientation = value);
        _services.GetService<QSeries.QSeriesPortWatcher>()?.AnnounceDisplayChange();
        return Applied(id);
    }

    private Nexus2ApplyResultDto ApplyLanguage(JsonElement root)
    {
        const string id = "language";
        var value = Nexus2Translator.TranslateLanguage(root);
        if (value is null)
        {
            return Skip(id, "no-supported-language");
        }
        _store.Update(s => s.Theme.Language = value);
        return Applied(id);
    }

    private PanelDeviceRecord? FindSingleInstanceRecord(string surface)
    {
        PanelDeviceRecord? best = null;
        foreach (var record in _panels.List())
        {
            if (!string.IsNullOrEmpty(record.DisplayId) || record.Capabilities?.Surface != surface)
            {
                continue;
            }
            if (best is null || record.LastSeenAt > best.LastSeenAt)
            {
                best = record;
            }
        }
        return best;
    }

    private static JsonElement? GetY70(JsonElement profile)
    {
        var widgets = Nexus2Json.GetObject(profile, "widgets");
        if (widgets is not { } w)
        {
            return null;
        }
        var faces = Nexus2Json.GetObject(w, "faces");
        return faces is { } f ? Nexus2Json.GetObject(f, "y70") : null;
    }

    private static JsonElement? GetQ60Software(JsonElement profile)
    {
        var q60 = Nexus2Json.GetObject(profile, "q60");
        return q60 is { } q ? Nexus2Json.GetObject(q, "software") : null;
    }

    private static Nexus2ApplyResultDto Applied(string id) => new() { Id = id, Status = "applied" };
    private static Nexus2ApplyResultDto Skip(string id, string detail) => new() { Id = id, Status = "skipped", Detail = detail };
    private static Nexus2ApplyResultDto Fail(string id, string detail) => new() { Id = id, Status = "failed", Detail = detail };
}
