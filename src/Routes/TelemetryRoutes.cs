using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Routes;

/// <summary>
/// Telemetry consent surface (dashboard-only, loopback). The Settings → General
/// toggle reads and flips the single anonymous-data opt-out that gates BOTH the
/// fleet heartbeat and product events. Dashboard-only by design - a paired phone
/// shouldn't be able to turn the whole install's telemetry on/off, so these are
/// <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/> (no .AllowPanel()).
///   GET  /telemetry/consent  -> { enabled }
///   POST /telemetry/consent  -> set + return { enabled }
/// A real true/false flip fires the opt_in/opt_out fleet event; the initial
/// welcome confirm (posting the same value the fresh-install default already
/// holds) is not a transition and fires nothing.
/// </summary>
internal static class TelemetryRoutes
{
    public static void MapTelemetryEndpoints(this WebApplication app)
    {
        app.MapGet("/telemetry/consent", (IConfigStore store) =>
            Results.Ok(new TelemetryConsentDto { Enabled = store.Load().Telemetry.CollectAnonymousData }))
            .LocalhostOnly();

        app.MapPost("/telemetry/consent", (TelemetryConsentBody body, IConfigStore store, FleetEventService fleet) =>
        {
            var was = store.Load().Telemetry.CollectAnonymousData;
            var now = body.Enabled;
            var transitionType = was == now ? null : (now ? TelemetryEvents.OptIn : TelemetryEvents.OptOut);

            if (transitionType is not null)
            {
                // Crash-safe ordering: the marker reaches disk before the
                // flag flips, so a crash mid-transition still has a pending
                // marker to recover on the next FleetTelemetryWorker pass.
                store.Update(s => s.Telemetry.FleetPendingConsentEvent = transitionType);
                store.FlushNow();
            }

            store.Update(s => s.Telemetry.CollectAnonymousData = now);

            if (transitionType is not null)
            {
                // Flush the flip too - otherwise a crash inside the debounce
                // window reverts CollectAnonymousData on disk while the
                // already-flushed marker still drives a retry that reports
                // the new value nexus-api/PostHog never actually persisted.
                store.FlushNow();
                // Off the request path - delivery has its own 10s timeout and
                // the persisted marker guarantees a retry if this is lost.
                _ = DeliverInBackground(fleet, transitionType);
            }

            return Results.Ok(new TelemetryConsentDto
            {
                Enabled = store.Load().Telemetry.CollectAnonymousData,
            });
        }).LocalhostOnly();
    }

    private static async Task DeliverInBackground(FleetEventService fleet, string transitionType)
    {
        try
        {
            await fleet.DeliverConsentTransitionAsync(transitionType, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fleet-event] consent delivery failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed class TelemetryConsentBody { public bool Enabled { get; set; } }
public sealed class TelemetryConsentDto { public bool Enabled { get; set; } }
