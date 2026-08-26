using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// First-run onboarding state (dashboard-only, loopback). The desktop
/// dashboard shows a one-time welcome screen on first launch, then a one-time
/// lighting device-selection screen, and marks each complete so it never
/// reappears, unless a factory reset wipes settings.json.
/// Dashboard-only by design - a paired phone has no onboarding, so these
/// are <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> (no
/// .AllowPanel()).
///   GET  /onboarding                    -> { completed, lightingCompleted }
///   POST /onboarding/complete           -> set completed, return status
///   POST /onboarding/lighting-complete  -> set lightingCompleted, return status
/// </summary>
internal static class OnboardingRoutes
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        // Existing installs start open; a fresh one holds notifications until
        // the sequence ends.
        var initial = app.Services.GetRequiredService<IConfigStore>().Load();
        Nexus.Service.Notifications.NotificationGate.Initialize(
            initial.OnboardingCompleted && initial.LightingOnboardingCompleted);

        app.MapGet("/onboarding", (IConfigStore store) =>
            Results.Ok(Status(store))).LocalhostOnly();

        app.MapPost("/onboarding/complete", (IConfigStore store) =>
        {
            store.Update(s => s.OnboardingCompleted = true);
            // A skip completes both flags at once; releasing here covers it.
            if (store.Load().LightingOnboardingCompleted)
            {
                _ = Nexus.Service.Notifications.NotificationGate.ReleaseAsync();
            }
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapPost("/onboarding/lighting-complete", (IConfigStore store) =>
        {
            store.Update(s => s.LightingOnboardingCompleted = true);
            // Last server-side step: anything held while the screens owned the
            // display goes out now.
            _ = Nexus.Service.Notifications.NotificationGate.ReleaseAsync();
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        // Replays the whole sequence. The two import latches are cleared as
        // well, or the import step is silently skipped - they are per-app flags
        // the migration routes own, not onboarding ones. An app that is no
        // longer installed still will not be offered: that gate also requires
        // live detection.
        app.MapPost("/onboarding/reset", (IConfigStore store) =>
        {
            store.Update(s =>
            {
                s.OnboardingCompleted = false;
                s.LightingOnboardingCompleted = false;
                s.Nexus2MigrationOffered = false;
                s.FanControlImportOffered = false;
            });
            // Re-close, or notifications keep firing through the replay.
            Nexus.Service.Notifications.NotificationGate.Initialize(onboardingComplete: false);
            return Results.Ok(Status(store));
        }).LocalhostOnly();
    }

    private static OnboardingStatusDto Status(IConfigStore store)
    {
        var s = store.Load();
        return new OnboardingStatusDto
        {
            Completed = s.OnboardingCompleted,
            LightingCompleted = s.LightingOnboardingCompleted,
        };
    }
}

public sealed class OnboardingStatusDto
{
    public bool Completed { get; set; }
    public bool LightingCompleted { get; set; }
}
