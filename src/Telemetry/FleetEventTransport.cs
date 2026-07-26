using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Telemetry;

/// <summary>
/// HTTP implementation of <see cref="IFleetEventTransport"/>. Mirrors
/// HeartbeatService's transport (IHttpClientFactory, 10s timeout, failures
/// logged and swallowed - a failed send never crashes the caller, it just
/// reports false so the event stays pending for the next retry).
/// </summary>
internal sealed class FleetEventTransport : IFleetEventTransport
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/events";

    private readonly IHttpClientFactory _http;

    public FleetEventTransport(IHttpClientFactory http) => _http = http;

    public async Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var content = JsonContent.Create(payload, AppJsonContext.Default.FleetEventPayload);
            using var res = await client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[fleet-event] {(int)res.StatusCode} from {Endpoint}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fleet-event] send failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
