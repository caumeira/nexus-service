using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Models.Profiles;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

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
                var s = store.Load();
                var prefs = new Preferences
                {
                    Theme = s.Theme,
                    Panel = s.Panel,
                    Overlay = s.Overlay,
                    Monitoring = s.Monitoring,
                    Cooling = new CoolingPrefs
                    {
                        FanChannelOrder = s.Cooling.FanChannelOrder,
                        PreferredCpuTempSensorId = s.Cooling.PreferredCpuTempSensorId,
                        PreferredGpuTempSensorId = s.Cooling.PreferredGpuTempSensorId,
                        PreferredGpuId = s.Cooling.PreferredGpuId,
                    },
                    Ui = s.Ui,
                    Update = new UpdatePrefs
                    {
                        AutoUpdateDisabled = s.Update.AutoUpdateDisabled,
                        UpdateChannel = s.Update.UpdateChannel,
                        LastDismissedUpdateVersion = s.Update.LastDismissedUpdateVersion,
                    },
                };
                // Profile switches swap the entire prefs block — everyone refetches via the broadcast.
                PanelTopics.BroadcastPrefs(hub);
                PanelTopics.BroadcastLighting(hub);
                PanelTopics.BroadcastCooling(hub);
                return Results.Ok(new SwitchProfileResponse { Switched = id, Prefs = prefs });
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
            var s = store.Load();
            return new Preferences
            {
                Theme = s.Theme,
                Panel = s.Panel,
                Overlay = s.Overlay,
                Monitoring = s.Monitoring,
                Cooling = new CoolingPrefs
                {
                    FanChannelOrder = s.Cooling.FanChannelOrder,
                    PreferredCpuTempSensorId = s.Cooling.PreferredCpuTempSensorId,
                    PreferredGpuTempSensorId = s.Cooling.PreferredGpuTempSensorId,
                    PreferredGpuId = s.Cooling.PreferredGpuId,
                },
                Ui = s.Ui,
                Update = new UpdatePrefs
                {
                    AutoUpdateDisabled = s.Update.AutoUpdateDisabled,
                    UpdateChannel = s.Update.UpdateChannel,
                    LastDismissedUpdateVersion = s.Update.LastDismissedUpdateVersion,
                },
            };
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
        app.MapPost("/profiles/{id}/reset", (string id, ProfileManager pm, ILightingProvider lp, IFanControlProvider fans, MultiplexHub hub, IConfigStore store) =>
        {
            try
            {
                // Halt the live lighting engine when the reset clears in-memory
                // lighting state: the named profile is the active one (reset
                // touches ALL categories on active), or the named profile is
                // the Primary and lighting is shared (the active profile's
                // in-memory shared lighting also clears to reflect the reset).
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
                // Re-engage engines from the freshly-defaulted settings so the
                // "on by default" cooling preset + lighting sync mode run
                // instead of leaving the engines idle.
                if (isActive)
                {
                    LiveEngineSync.Apply(store, fans, lp);
                }
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
        app.MapPost("/profiles/{id}/reset/{category}", (string id, string category, ProfileManager pm, ILightingProvider lp, IFanControlProvider fans, MultiplexHub hub, IConfigStore store) =>
        {
            try
            {
                var normalized = ProfileSharing.Normalize(category);
                var settings = store.Load();
                var isActive = id == pm.GetManifest().ActiveProfileId;
                // Capture the shared flag for the reset category BEFORE the
                // reset runs, so the post-reset re-engage decision reads the
                // value that was in force at request time.
                var categoryIsShared = normalized != null
                    && settings.SharedCategories.Contains(normalized);
                if (normalized == ProfileSharing.Lighting)
                {
                    // StopAll only when the reset flips the live engine: shared
                    // category writes through to in-memory active state (always
                    // changes), or per-profile reset on the active profile.
                    // Per-profile reset on a different profile only touches a
                    // stored JSON, so leave the live engine alone.
                    if (categoryIsShared || isActive)
                    {
                        try
                        { lp.StopAll(); }
                        catch { }
                    }
                }
                pm.ResetCategory(id, category);
                // Re-engage engines from the freshly-defaulted settings when
                // the live state changed (active profile, or shared category
                // that writes through to active).
                if (isActive || categoryIsShared)
                {
                    LiveEngineSync.Apply(store, fans, lp);
                }
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

        app.MapPost("/preferences", (PreferencesPatch body, IConfigStore store, ProfileManager pm, MultiplexHub hub) =>
        {
            store.Update(s =>
            {
                if (body.Theme is { } theme)
                {
                    if (theme.Language is not null)    s.Theme.Language    = theme.Language;
                    if (theme.ThemeMode is not null)   s.Theme.ThemeMode   = theme.ThemeMode;
                    if (theme.AccentColor is not null) s.Theme.AccentColor = theme.AccentColor;
                    if (theme.ResolvedThemeMode is not null) s.Theme.ResolvedThemeMode = theme.ResolvedThemeMode;
                }
                if (body.Panel is { } panel)
                {
                    if (panel.AutoLaunch.HasValue)               s.Panel.AutoLaunch            = panel.AutoLaunch.Value;
                    if (panel.ReserveMonitor.HasValue)           s.Panel.ReserveMonitor        = panel.ReserveMonitor.Value;
                    if (panel.ThemeSyncWithDesktop.HasValue)     s.Panel.ThemeSyncWithDesktop  = panel.ThemeSyncWithDesktop.Value;
                    if (panel.ThemeMode is not null)             s.Panel.ThemeMode             = panel.ThemeMode;
                    if (panel.AccentSyncWithDesktop.HasValue)    s.Panel.AccentSyncWithDesktop = panel.AccentSyncWithDesktop.Value;
                    if (panel.AccentColor is not null)           s.Panel.AccentColor           = panel.AccentColor;
                    if (panel.BackgroundColor is not null)       s.Panel.BackgroundColor       = panel.BackgroundColor;
                    if (panel.BackgroundColorLight is not null)  s.Panel.BackgroundColorLight  = panel.BackgroundColorLight;
                    if (panel.BackgroundMode is not null)        s.Panel.BackgroundMode        = panel.BackgroundMode;
                    if (panel.BackgroundEffect is not null)      s.Panel.BackgroundEffect      = panel.BackgroundEffect;
                    if (panel.BackgroundTemplate.HasValue)       s.Panel.BackgroundTemplate    = panel.BackgroundTemplate.Value;
                    if (panel.BackgroundOpacity.HasValue)        s.Panel.BackgroundOpacity     = panel.BackgroundOpacity.Value;
                    if (panel.PanelOpacity.HasValue)             s.Panel.PanelOpacity          = Math.Clamp(panel.PanelOpacity.Value, 0.0, 1.0);
                    if (panel.WidgetOpacity.HasValue)            s.Panel.WidgetOpacity         = panel.WidgetOpacity.Value;
                    if (panel.WidgetLabels.HasValue)             s.Panel.WidgetLabels          = panel.WidgetLabels.Value;
                    if (panel.DashboardLayout is not null)       s.Panel.DashboardLayout       = panel.DashboardLayout;
                }
                if (body.Overlay is { } overlay)
                {
                    if (overlay.Enabled.HasValue)      s.Overlay.Enabled     = overlay.Enabled.Value;
                    if (overlay.AlwaysOnTop.HasValue)  s.Overlay.AlwaysOnTop = overlay.AlwaysOnTop.Value;
                    if (overlay.Scale.HasValue)
                    {
                        var newScale = Math.Clamp(overlay.Scale.Value, 50, 200);
                        var oldScale = s.Overlay.Scale > 0 ? s.Overlay.Scale : 100;
                        // Rescale every pinned widget's (col, row) so the *visual*
                        // position stays put when the cell-size scale changes.
                        // ratio = old / new because at 100→200% a widget at col 4
                        // should land at col 2 (half as many cells cover the same
                        // pixels). Snap to the 0.25 drag grid and clamp ≥0; max
                        // clamp happens client-side where monitor size is known.
                        if (oldScale != newScale && s.Overlay.Layout is { } layout)
                        {
                            var ratio = (double)oldScale / newScale;
                            foreach (var w in layout)
                            {
                                w.Col = Math.Max(0, Math.Round(w.Col * ratio * 4) / 4);
                                w.Row = Math.Max(0, Math.Round(w.Row * ratio * 4) / 4);
                            }
                        }
                        s.Overlay.Scale = newScale;
                    }
                    if (overlay.Opacity.HasValue)
                    {
                        s.Overlay.Opacity = Math.Clamp(overlay.Opacity.Value, 0, 1);
                    }
                    if (overlay.Monitor.HasValue)
                    {
                        // -1 (primary) or any non-negative index. Don't clamp to a
                        // max here — the overlay host validates against the
                        // enumerated monitor count and falls back to primary if
                        // the index is out of range.
                        var v = overlay.Monitor.Value;
                        s.Overlay.Monitor = v < -1 ? -1 : v;
                    }
                    if (overlay.Layout is not null) s.Overlay.Layout = overlay.Layout;
                }
                if (body.Monitoring is { } monitoring)
                {
                    if (monitoring.ShowAverage.HasValue)         s.Monitoring.ShowAverage          = monitoring.ShowAverage.Value;
                    if (monitoring.ShowMacStatusBarIcon.HasValue) s.Monitoring.ShowMacStatusBarIcon = monitoring.ShowMacStatusBarIcon.Value;
                    if (monitoring.ShowWindowsTrayIcon.HasValue)  s.Monitoring.ShowWindowsTrayIcon  = monitoring.ShowWindowsTrayIcon.Value;
                    if (monitoring.DetailedCollapsed is not null) s.Monitoring.DetailedCollapsed   = monitoring.DetailedCollapsed;
                }
                if (body.Cooling is { } cooling)
                {
                    if (cooling.FanChannelOrder is not null) s.Cooling.FanChannelOrder = cooling.FanChannelOrder;
                    // Empty string is a meaningful "clear back to auto" value, distinct
                    // from null which means "client didn't send this field".
                    if (cooling.PreferredCpuTempSensorId is not null)
                        s.Cooling.PreferredCpuTempSensorId = cooling.PreferredCpuTempSensorId.Length == 0 ? null : cooling.PreferredCpuTempSensorId;
                    if (cooling.PreferredGpuTempSensorId is not null)
                        s.Cooling.PreferredGpuTempSensorId = cooling.PreferredGpuTempSensorId.Length == 0 ? null : cooling.PreferredGpuTempSensorId;
                    if (cooling.PreferredGpuId is not null)
                        s.Cooling.PreferredGpuId = cooling.PreferredGpuId.Length == 0 ? null : cooling.PreferredGpuId;
                }
                if (body.Ui is { } ui)
                {
                    if (ui.DisableConflictAlerts.HasValue) s.Ui.DisableConflictAlerts = ui.DisableConflictAlerts.Value;
                }
                if (body.Update is { } update)
                {
                    if (update.AutoUpdateDisabled.HasValue) s.Update.AutoUpdateDisabled = update.AutoUpdateDisabled.Value;
                    if (update.UpdateChannel is not null) s.Update.UpdateChannel = update.UpdateChannel;
                    if (update.LastDismissedUpdateVersion is not null) s.Update.LastDismissedUpdateVersion = update.LastDismissedUpdateVersion;
                }
            });
            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);
            return ApiResponse.Ok();
        }).AllowPanel();
    }
}
