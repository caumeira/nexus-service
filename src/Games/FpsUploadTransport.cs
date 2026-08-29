using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Games;

/// <summary>Outcome of one upload attempt; distinct from IFleetEventTransport's
/// bool because a 400 (reject, do not retry) and a 5xx/network failure
/// (retry later) need different store-side handling.</summary>
public enum FpsUploadResult { Success, Rejected, Failed }

/// <summary>Delivers one fps-session batch to nexus-api; abstracted so
/// FpsUploadWorker is testable without a real HTTP call.</summary>
internal interface IFpsUploadTransport
{
    Task<FpsUploadResult> SendAsync(FpsUploadPayload payload, CancellationToken ct);
}

/// <summary>Mirrors FleetEventTransport's shape (client credential, source-gen
/// JsonContent, never throws) against the fps ingest endpoint.</summary>
internal sealed class FpsUploadTransport : IFpsUploadTransport
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/fps-sessions";

    private readonly IHttpClientFactory _http;

    public FpsUploadTransport(IHttpClientFactory http) => _http = http;

    public async Task<FpsUploadResult> SendAsync(FpsUploadPayload payload, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            Common.ClientCredential.Apply(client);
            using var content = JsonContent.Create(payload, AppJsonContext.Default.FpsUploadPayload);
            using var res = await client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);

            if (res.IsSuccessStatusCode)
            {
                return FpsUploadResult.Success;
            }

            if (res.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Console.Error.WriteLine($"[fps-upload] 400 from {Endpoint}: {body}");
                return FpsUploadResult.Rejected;
            }

            Console.Error.WriteLine($"[fps-upload] {(int)res.StatusCode} from {Endpoint}");
            return FpsUploadResult.Failed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fps-upload] send failed: {ex.GetType().Name}: {ex.Message}");
            return FpsUploadResult.Failed;
        }
    }
}
