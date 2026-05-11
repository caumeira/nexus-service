using Qos.Service.Auth;
using Qos.Service.Lighting;
using Qos.Service.Models;
using Qos.Service.Models.Profiles;
using Qos.Service.Persistence;
using Qos.Service.Sockets;

namespace Qos.Service.Routes;

public static class ProfileRoutes
{
    public static void MapProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/profiles", (ProfileManager pm) =>
        {
            var manifest = pm.GetManifest();
            return new ListProfilesResponse
            {
                Profiles = manifest.Profiles,
                ActiveId = manifest.ActiveProfileId,
            };
        });

        app.MapPost("/profiles/create", (CreateProfileBody body, ProfileManager pm) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(ApiResponse.Fail("Name is required."));
            }

            try
            {
                var entry = pm.CreateProfile(body.Name);
                return Results.Ok(new ProfileResponse { Profile = entry });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        app.MapPost("/profiles/{id}/switch", (string id, ProfileManager pm, IConfigStore store, MultiplexHub hub) =>
        {
            try
            {
                pm.SwitchProfile(id);
                var ui = store.Load().Ui;
                // Profile switches swap the entire Ui block - everyone refetches.
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(new SwitchProfileResponse { Switched = id, Ui = ui });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapPost("/profiles/{id}/rename", (string id, RenameProfileBody body, ProfileManager pm) =>
        {
            if (string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(ApiResponse.Fail("Name is required."));
            }

            try
            {
                pm.RenameProfile(id, body.Name);
                var manifest = pm.GetManifest();
                var entry = manifest.Profiles.Find(p => p.Id == id);
                return Results.Ok(new ProfileResponse { Profile = entry });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapPost("/profiles/{id}/save", (string id, ProfileManager pm) =>
        {
            pm.SaveActiveProfile();
            return ApiResponse.Ok();
        });

        app.MapDelete("/profiles/{id}", (string id, ProfileManager pm) =>
        {
            try
            {
                pm.DeleteProfile(id);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapGet("/profiles/{id}/export", (string id, ProfileManager pm) =>
        {
            var json = pm.ExportProfileJson(id);
            if (json == null)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }

            return Results.Text(json, "application/json");
        });

        app.MapPost("/profiles/import", async (HttpRequest req, ProfileManager pm) =>
        {
            var json = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
            try
            {
                var entry = pm.ImportProfileJson(json);
                return Results.Ok(new ProfileResponse { Profile = entry });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(ApiResponse.Fail("Invalid profile JSON."));
            }
        });

        app.MapGet("/preferences", (IConfigStore store) =>
        {
            return store.Load().Ui;
        }).AllowPanel();

        // Read current sharing config: which profile is the Primary, which
        // categories are Shared, and the full list of category ids the UI
        // can render.
        app.MapGet("/profiles/sharing", (IConfigStore store) =>
        {
            var s = store.Load();
            return new SharingResponse
            {
                PrimaryProfileId = s.PrimaryProfileId,
                SharedCategories = new List<string>(s.SharedCategories),
                AllCategories = new List<string>(ProfileSharing.All),
            };
        });

        app.MapPut("/profiles/sharing/primary", (SetPrimaryBody body, ProfileManager pm, MultiplexHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.ProfileId))
            {
                return Results.BadRequest(ApiResponse.Fail("profileId is required."));
            }
            try
            {
                pm.SetPrimary(body.ProfileId);
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        app.MapPut("/profiles/sharing/categories", (SetCategorySharedBody body, ProfileManager pm, MultiplexHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.Category))
            {
                return Results.BadRequest(ApiResponse.Fail("category is required."));
            }
            try
            {
                var changed = pm.SetCategoryShared(body.Category, body.Shared);
                if (changed)
                {
                    // Toggling a category preserves its data (we never wipe to
                    // defaults here), so the lighting engine MUST keep running
                    // - calling StopAll without a follow-up start request kills
                    // the live effect. Profile switch uses the same machinery:
                    // just broadcast, let the client refetch and resync.
                    PanelTopics.BroadcastPrefs(hub);
                    PanelTopics.BroadcastLighting(hub);
                    PanelTopics.BroadcastCooling(hub);
                }
                return Results.Ok(ApiResponse.Ok());
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        // Reset every per-profile (non-shared) category on the named profile.
        // Shared categories are untouched. If the named profile is the active
        // one, in-memory state is updated and a broadcast fires; otherwise the
        // reset only touches that profile's JSON on disk.
        app.MapPost("/profiles/{id}/reset", (string id, ProfileManager pm, ILightingProvider lp, MultiplexHub hub, IConfigStore store) =>
        {
            try
            {
                // Halt the live lighting engine when the reset will actually
                // clear in-memory lighting state: the named profile is the
                // active one (we now reset ALL categories on active), or the
                // named profile is the Primary and lighting is shared (we
                // also clear the active profile's in-memory shared lighting
                // so it reflects the Primary's reset).
                var settings = store.Load();
                var isActive = id == pm.GetManifest().ActiveProfileId;
                var isPrimary = settings.PrimaryProfileId == id;
                var lightingShared = settings.SharedCategories.Contains(ProfileSharing.Lighting);
                if (isActive || (isPrimary && lightingShared))
                {
                    try
                    { lp.StopAll(); }
                    catch { }
                }
                pm.ResetProfile(id);
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
        });

        // Reset a single category. If the category is shared, the reset is
        // redirected to the Primary's data and affects every profile. If the
        // category is per-profile, only the named profile's JSON is touched
        // (and in-memory state if the named profile is active).
        app.MapPost("/profiles/{id}/reset/{category}", (string id, string category, ProfileManager pm, ILightingProvider lp, MultiplexHub hub, IConfigStore store) =>
        {
            try
            {
                var normalized = ProfileSharing.Normalize(category);
                if (normalized == ProfileSharing.Lighting)
                {
                    // StopAll only when the reset will actually flip the live
                    // engine: shared category writes through to in-memory
                    // active state (always changes), or per-profile reset on
                    // the active profile. Per-profile reset on a different
                    // profile only touches a stored JSON, so leave the live
                    // engine alone.
                    var settings = store.Load();
                    var isActive = id == pm.GetManifest().ActiveProfileId;
                    var isShared = settings.SharedCategories.Contains(ProfileSharing.Lighting);
                    if (isShared || isActive)
                    {
                        try
                        { lp.StopAll(); }
                        catch { }
                    }
                }
                pm.ResetCategory(id, category);
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(ApiResponse.Ok());
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(ApiResponse.Fail("Profile not found."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponse.Fail(ex.Message));
            }
        });

        app.MapPost("/preferences", (UiSettingsPatch body, IConfigStore store, ProfileManager pm, MultiplexHub hub) =>
        {
            store.Update(s =>
            {
                if (body.Language is not null)
                    s.Ui.Language = body.Language;
                if (body.ThemeMode is not null)
                    s.Ui.ThemeMode = body.ThemeMode;
                if (body.AccentColor is not null)
                    s.Ui.AccentColor = body.AccentColor;
                if (body.DisableConflictAlerts.HasValue)
                    s.Ui.DisableConflictAlerts = body.DisableConflictAlerts.Value;
                if (body.MonitoringShowAverage.HasValue)
                    s.Ui.MonitoringShowAverage = body.MonitoringShowAverage.Value;
                if (body.MonitoringDetailedCollapsed is not null)
                    s.Ui.MonitoringDetailedCollapsed = body.MonitoringDetailedCollapsed;
                if (body.ShowMacStatusBarIcon.HasValue)
                    s.Ui.ShowMacStatusBarIcon = body.ShowMacStatusBarIcon.Value;
                if (body.ShowWindowsTrayIcon.HasValue)
                    s.Ui.ShowWindowsTrayIcon = body.ShowWindowsTrayIcon.Value;
                if (body.PanelAutoLaunch.HasValue)
                    s.Ui.PanelAutoLaunch = body.PanelAutoLaunch.Value;
                if (body.FanChannelOrder is not null)
                    s.Ui.FanChannelOrder = body.FanChannelOrder;
                if (body.PanelThemeSyncWithDesktop.HasValue)
                    s.Ui.PanelThemeSyncWithDesktop = body.PanelThemeSyncWithDesktop.Value;
                if (body.PanelThemeMode is not null)
                    s.Ui.PanelThemeMode = body.PanelThemeMode;
                if (body.PanelAccentSyncWithDesktop.HasValue)
                    s.Ui.PanelAccentSyncWithDesktop = body.PanelAccentSyncWithDesktop.Value;
                if (body.PanelAccentColor is not null)
                    s.Ui.PanelAccentColor = body.PanelAccentColor;
                if (body.PanelBackgroundColor is not null)
                    s.Ui.PanelBackgroundColor = body.PanelBackgroundColor;
                if (body.PanelBackgroundColorLight is not null)
                    s.Ui.PanelBackgroundColorLight = body.PanelBackgroundColorLight;
                if (body.PanelBackgroundMode is not null)
                    s.Ui.PanelBackgroundMode = body.PanelBackgroundMode;
                if (body.PanelBackgroundEffect is not null)
                    s.Ui.PanelBackgroundEffect = body.PanelBackgroundEffect;
                if (body.PanelBackgroundTemplate.HasValue)
                    s.Ui.PanelBackgroundTemplate = body.PanelBackgroundTemplate.Value;
                if (body.PanelBackgroundOpacity.HasValue)
                    s.Ui.PanelBackgroundOpacity = body.PanelBackgroundOpacity.Value;
                if (body.PanelWidgetOpacity.HasValue)
                    s.Ui.PanelWidgetOpacity = body.PanelWidgetOpacity.Value;
                if (body.PanelWidgetLabels.HasValue)
                    s.Ui.PanelWidgetLabels = body.PanelWidgetLabels.Value;
                if (body.DashboardLayout is not null)
                    s.Ui.DashboardLayout = body.DashboardLayout;
                if (body.OverlayWidgetsEnabled.HasValue)
                    s.Ui.OverlayWidgetsEnabled = body.OverlayWidgetsEnabled.Value;
                if (body.OverlayWidgetsAlwaysOnTop.HasValue)
                    s.Ui.OverlayWidgetsAlwaysOnTop = body.OverlayWidgetsAlwaysOnTop.Value;
                if (body.OverlayWidgetScale.HasValue)
                {
                    var newScale = Math.Clamp(body.OverlayWidgetScale.Value, 50, 200);
                    var oldScale = s.Ui.OverlayWidgetScale > 0 ? s.Ui.OverlayWidgetScale : 100;
                    // When the scale changes, rescale every widget's
                    // (col, row) so the *visual* position stays put.
                    // ratio = old / new because at 100->200% a widget at
                    // col 4 should now be at col 2 (half as many cells
                    // covers the same pixels). Snap to the 0.25 drag grid
                    // and clamp to >=0 (max-clamping happens client-side
                    // where the monitor size is known).
                    if (oldScale != newScale)
                    {
                        var ratio = (double)oldScale / newScale;
                        foreach (var w in s.Ui.OverlayLayout)
                        {
                            w.Col = Math.Max(0, Math.Round(w.Col * ratio * 4) / 4);
                            w.Row = Math.Max(0, Math.Round(w.Row * ratio * 4) / 4);
                        }
                    }
                    s.Ui.OverlayWidgetScale = newScale;
                }
                if (body.OverlayWidgetOpacity.HasValue)
                {
                    s.Ui.OverlayWidgetOpacity = Math.Clamp(body.OverlayWidgetOpacity.Value, 0, 1);
                }
                if (body.OverlayLayout is not null)
                    s.Ui.OverlayLayout = body.OverlayLayout;
            });
            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
    }
}
