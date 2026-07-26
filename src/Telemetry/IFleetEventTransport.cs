using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>Delivers one fleet event to nexus-api; abstracted so <see cref="FleetEventService"/> is testable without a real HTTP call.</summary>
internal interface IFleetEventTransport
{
    /// <returns>True on a 2xx response; false on any failure (never throws).</returns>
    Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct);
}
