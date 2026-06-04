using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Routes;

/// <summary>
/// Telemetry consent surface (dashboard-only, loopback). The Settings → General
/// toggle reads and flips the single anonymous-data opt-out that gates BOTH the
/// fleet heartbeat and product events. Dashboard-only by design — a paired phone
/// shouldn't be able to turn the whole install's telemetry on/off, so these are
/// <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> (no .AllowPanel()).
///   GET  /telemetry/consent  -> { enabled }
///   POST /telemetry/consent  -> set + return { enabled }
/// </summary>
internal static class TelemetryRoutes
{
    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapGet("/telemetry/consent", (IConfigStore store) =>
            Results.Ok(new TelemetryConsentDto { Enabled = store.Load().Telemetry.CollectAnonymousData }))
            .LocalhostOnly();

        app.MapPost("/telemetry/consent", (TelemetryConsentBody body, IConfigStore store) =>
        {
            store.Update(s => s.Telemetry.CollectAnonymousData = body.Enabled);
            return Results.Ok(new TelemetryConsentDto
            {
                Enabled = store.Load().Telemetry.CollectAnonymousData,
            });
        }).LocalhostOnly();
    }
}

public sealed class TelemetryConsentBody { public bool Enabled { get; set; } }
public sealed class TelemetryConsentDto { public bool Enabled { get; set; } }
