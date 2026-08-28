using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>Fleet transport for a build with no credential. Reports failure, never success: FleetEventService persists delivered-state into settings.json, so a fake success would permanently suppress this machine's install and consent events even after it is rebuilt with a credential. Nothing retries it either - FleetTelemetryWorker is not hosted in this shape.</summary>
internal sealed class NullFleetEventTransport : IFleetEventTransport
{
    public Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct) => Task.FromResult(false);
}
