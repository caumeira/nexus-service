using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Builds and delivers the fleet events (install, specs, opt_out, opt_in) to
/// both sinks: nexus-api's /telemetry/events (the system of record - tracked
/// with persisted per-event delivered state and retried until it succeeds)
/// and PostHog (a corroborating product event, best-effort, not retried
/// beyond the attempts nexus-api delivery already forces). Called both by the
/// consent route (immediate attempt on a real transition) and by
/// <see cref="FleetTelemetryWorker"/> (boot + hourly retry pass).
/// </summary>
internal sealed class FleetEventService
{
    private readonly IConfigStore _store;
    private readonly IFleetEventTransport _transport;
    private readonly ITelemetry _telemetry;
    private readonly IReadOnlyList<ITelemetrySink> _sinks;
    private readonly SystemSpecsCollector _specs;

    // Not persisted: bounds the PostHog leg of the install event to one
    // attempt per process lifetime, so a nexus-api outage that forces many
    // hourly retries doesn't also resend the PostHog event every retry.
    private bool _postHogInstallAttempted;

    public FleetEventService(
        IConfigStore store,
        IFleetEventTransport transport,
        ITelemetry telemetry,
        IEnumerable<ITelemetrySink> sinks,
        SystemSpecsCollector specs)
    {
        _store = store;
        _transport = transport;
        _telemetry = telemetry;
        _sinks = sinks.Where(s => s.Enabled).ToArray();
        _specs = specs;
    }

    /// <summary>
    /// One retry pass: deliver a pending consent-transition event regardless
    /// of the current consent value (the sanctioned opt-out exception), then
    /// - only while opted in - deliver the install event once and re-send
    /// specs when the summary changed.
    /// </summary>
    public async Task RunPendingRetriesAsync(CancellationToken ct)
    {
        var pending = _store.Load().Telemetry.FleetPendingConsentEvent;
        if (!string.IsNullOrEmpty(pending))
            await DeliverConsentTransitionAsync(pending, ct).ConfigureAwait(false);

        if (!_store.Load().Telemetry.CollectAnonymousData)
            return;

        if (!_store.Load().Telemetry.FleetInstallDelivered)
            await DeliverInstallAsync(ct).ConfigureAwait(false);

        await MaybeDeliverSpecsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers the opt_out/opt_in event for a just-recorded consent flip.
    /// The caller has already persisted <paramref name="type"/> into
    /// FleetPendingConsentEvent before flipping CollectAnonymousData; this
    /// clears that marker only once nexus-api confirms delivery.
    /// </summary>
    public async Task DeliverConsentTransitionAsync(string type, CancellationToken ct)
    {
        var installId = InstallIdentity.ResolveStored(_store);
        if (installId is null)
        {
            // Never had an id to attribute this to (opted out before any
            // event ever minted one) - nothing to deliver.
            _store.Update(s => s.Telemetry.FleetPendingConsentEvent = "");
            return;
        }

        var payload = BuildEnvelope(type, installId);
        var delivered = await _transport.SendAsync(payload, ct).ConfigureAwait(false);

        if (type == TelemetryEvents.OptOut)
        {
            // Both consent-gated pipelines are closed at this point (the
            // flag already flipped false): send directly to each sink,
            // bypassing ITelemetry.Capture and its opt-out gate.
            var ev = new TelemetryEvent
            {
                Name = TelemetryEvents.OptOut,
                Timestamp = DateTimeOffset.UtcNow,
                Properties = new[] { new KeyValuePair<string, object?>("version", payload.Version) },
            };
            foreach (var sink in _sinks)
            {
                try { await sink.SendAsync(installId, new[] { ev }, ct).ConfigureAwait(false); }
                catch (Exception ex) { Console.Error.WriteLine($"[fleet-event] opt_out sink failed: {ex.Message}"); }
            }
        }
        else
        {
            // opt_in: CollectAnonymousData is already true by this point, so
            // the normal product-events pipeline is open.
            _telemetry.Capture(type, ("version", payload.Version));
        }

        if (delivered)
            _store.Update(s => s.Telemetry.FleetPendingConsentEvent = "");
    }

    private async Task DeliverInstallAsync(CancellationToken ct)
    {
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;

        var payload = BuildEnvelope(TelemetryEvents.Install, installId);
        var delivered = await _transport.SendAsync(payload, ct).ConfigureAwait(false);

        if (!_postHogInstallAttempted)
        {
            _postHogInstallAttempted = true;
            _telemetry.Capture(TelemetryEvents.Install, ("version", payload.Version), ("os", payload.Os));
        }

        if (delivered)
            _store.Update(s => s.Telemetry.FleetInstallDelivered = true);
    }

    private async Task MaybeDeliverSpecsAsync(CancellationToken ct)
    {
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;

        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(specs.Processor) && string.IsNullOrWhiteSpace(specs.GraphicsCard))
            return; // not ready yet (cold boot) - retry next pass.

        var gpu = specs.GraphicsCard.Split(
            " + ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ramBytes = SystemProfileService.ParseRamGb(specs.Memory) is int gb ? (long)gb * 1024 * 1024 * 1024 : 0;

        var hash = ComputeSpecsHash(specs.Processor, gpu, ramBytes, specs.Motherboard);
        if (hash == _store.Load().Telemetry.FleetSpecsHash)
            return; // unchanged since the last successful send.

        var payload = BuildEnvelope(TelemetryEvents.Specs, installId);
        payload.Specs = new FleetEventSpecs
        {
            Cpu = specs.Processor,
            Gpu = gpu,
            RamBytes = ramBytes,
            Motherboard = specs.Motherboard,
        };

        if (!await _transport.SendAsync(payload, ct).ConfigureAwait(false))
            return;

        _store.Update(s => s.Telemetry.FleetSpecsHash = hash);
        _telemetry.Capture(TelemetryEvents.Specs,
            ("cpu", specs.Processor), ("gpu", gpu), ("ram_bytes", ramBytes), ("motherboard", specs.Motherboard));
    }

    private static FleetEventPayload BuildEnvelope(string type, string installId) => new()
    {
        InstallId = installId,
        Type = type,
        DeviceType = "desktop",
        Version = BuildInfo.Version,
        Os = TelemetryPlatform.OsTag(),
        OsVersion = RuntimeInformation.OSDescription,
        Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
    };

    // Unit-separator delimiter so a field value containing the delimiter
    // can't collide two different summaries into the same hash input.
    internal static string ComputeSpecsHash(string cpu, IReadOnlyList<string> gpu, long ramBytes, string motherboard)
    {
        var input = string.Join('\u001f', cpu, string.Join('\u001f', gpu), ramBytes.ToString(), motherboard);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
