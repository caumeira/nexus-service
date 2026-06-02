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
/// (DAU / concurrents / version / geo) — no PII. Gated by the
/// "collect anonymous data" setting (default on); off ⇒ no beat, and the
/// install id is forgotten. Server derives location from the Cloudflare edge.
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
        // PeriodicTimer drops drift if a beat runs long. The do/while beats once
        // at boot, then on each tick.
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
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
        var settings = _store.Load();
        if (!settings.Telemetry.CollectAnonymousData)
        {
            // Opted out: forget the id so re-enabling looks like a fresh install.
            if (!string.IsNullOrEmpty(settings.Telemetry.InstallId))
            {
                _store.Update(s => s.Telemetry.InstallId = "");
            }
            return;
        }

        var installId = settings.Telemetry.InstallId;
        if (string.IsNullOrEmpty(installId))
        {
            installId = Guid.NewGuid().ToString("N");
            _store.Update(s => s.Telemetry.InstallId = installId);
        }

        var payload = new HeartbeatPayload
        {
            InstallId = installId,
            DeviceType = "desktop",
            Version = BuildInfo.Version,
            Os = OsTag(),
            OsVersion = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
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

    private static string OsTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "mac";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "other";
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
