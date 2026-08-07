using Microsoft.AspNetCore.Builder;
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
        app.MapGet("/onboarding", (IConfigStore store) =>
            Results.Ok(Status(store))).LocalhostOnly();

        app.MapPost("/onboarding/complete", (IConfigStore store) =>
        {
            store.Update(s => s.OnboardingCompleted = true);
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapPost("/onboarding/lighting-complete", (IConfigStore store) =>
        {
            store.Update(s => s.LightingOnboardingCompleted = true);
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
