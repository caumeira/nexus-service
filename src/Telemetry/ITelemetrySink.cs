using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>
/// A destination for a batch of telemetry events. PostHog is the only sink
/// today; a future first-party sink (own /telemetry/event → Postgres) plugs in
/// here — register it in DI and it receives the same batches, with zero
/// call-site changes.
/// </summary>
internal interface ITelemetrySink
{
    /// <summary>True when this sink is configured well enough to send (e.g. a
    /// PostHog key is present). Disabled sinks are skipped entirely.</summary>
    bool Enabled { get; }

    /// <param name="distinctId">The anonymous install id for every event in the batch.</param>
    Task SendAsync(string distinctId, IReadOnlyList<TelemetryEvent> batch, CancellationToken ct);
}
