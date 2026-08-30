using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Anonymous fleet-presence heartbeat. POSTs to api.hellonexus.com at boot and
/// every 5 minutes so we can see active installs, versions, and rough location
/// (DAU / concurrents / version / geo) - no PII. Gated by "collect anonymous
/// data" (true by default only on a fresh install); off means no beat, but
/// the id is kept for a later opt-in. Server derives location from the
/// Cloudflare edge.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/heartbeat";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IConfigStore _store;
    private readonly IHttpClientFactory _http;

    public HeartbeatService(IConfigStore store, IHttpClientFactory http)
    {
        _store = store;
        _http = http;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Fleet counts describe our installs only; a build we did not publish must not inflate them.
        if (Common.ClientCredential.IsOfficial is false)
            return;

        // PeriodicTimer drops drift if a beat runs long. The do/while beats once
        // at boot, then on each tick.
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (!Nexus.Service.Games.GameModeNetworkGate.IsHeld)
                    await BeatAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed beat must never take the worker down.
                Console.Error.WriteLine(
                    $"[heartbeat] beat failed: {ex.GetType().Name}: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        // Shared with product telemetry: honors the single opt-out but the id itself survives - see InstallIdentity.
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
            return;

        var payload = new HeartbeatPayload
        {
            InstallId = installId,
            DeviceType = "desktop",
            Version = BuildInfo.Version,
            Os = TelemetryPlatform.OsTag(),
            OsVersion = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        Common.ClientCredential.Apply(client);
        using var content = JsonContent.Create(
            payload, AppJsonContext.Default.HeartbeatPayload);
        using var res = await client
            .PostAsync(Endpoint, content, ct)
            .ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[heartbeat] {(int)res.StatusCode} from {Endpoint}");
        }
    }
}

/// <summary>Anonymous heartbeat body. Serialized camelCase via AppJsonContext.</summary>
public sealed class HeartbeatPayload
{
    public string InstallId { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Arch { get; set; } = "";
}
