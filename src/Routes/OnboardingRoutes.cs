using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// First-run welcome screen state (dashboard-only, loopback). The desktop
/// dashboard shows a one-time welcome screen on first launch and marks it
/// complete so it never reappears, unless a factory reset wipes settings.json.
/// Dashboard-only by design - a paired phone has no welcome screen, so these
/// are <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> (no
/// .AllowPanel()).
///   GET  /onboarding           -> { completed }
///   POST /onboarding/complete  -> set true, return { completed: true }
/// </summary>
internal static class OnboardingRoutes
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        app.MapGet("/onboarding", (IConfigStore store) =>
            Results.Ok(new OnboardingStatusDto { Completed = store.Load().OnboardingCompleted }))
            .LocalhostOnly();

        app.MapPost("/onboarding/complete", (IConfigStore store) =>
        {
            store.Update(s => s.OnboardingCompleted = true);
            return Results.Ok(new OnboardingStatusDto
            {
                Completed = store.Load().OnboardingCompleted,
            });
        }).LocalhostOnly();
    }
}

public sealed class OnboardingStatusDto { public bool Completed { get; set; } }
