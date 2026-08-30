using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Nexus.Service.Telemetry;

namespace Nexus.Service.Games;

/// <summary>
/// Uploads persisted, pending fps sessions to nexus-api at boot and hourly,
/// mirroring FleetTelemetryWorker's cadence. Gated the same way fleet
/// telemetry is: no upload while CollectAnonymousData is off, and only
/// hosted at all in an official build (see NexusServiceCollectionExtensions,
/// the ingest endpoint requires the signed client credential).
/// </summary>
internal sealed class FpsUploadWorker : BackgroundService
{
    private const int MaxSessionsPerBatch = 50;

    // Structural bounds nexus-api's UploadFpsSessionsDto enforces (upload-fps-sessions.dto.ts).
    private const int MaxDispDimension = 16384;
    private const int MaxRefreshHz = 1000;
    private const int MaxFocusedSec = 86400;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IConfigStore _store;
    private readonly BinaryFpsSessionStore _sessions;
    private readonly IFpsUploadTransport _transport;
    private readonly SystemSpecsCollector _specs;

    public FpsUploadWorker(
        IConfigStore store, BinaryFpsSessionStore sessions, IFpsUploadTransport transport, SystemSpecsCollector specs)
    {
        _store = store;
        _sessions = sessions;
        _transport = transport;
        _specs = specs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (!GameModeNetworkGate.IsHeld)
                    await RunPendingUploadsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[fps-upload] pass failed: {ex.GetType().Name}: {ex.Message}");
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

    internal async Task RunPendingUploadsAsync(CancellationToken ct)
    {
        var settings = _store.Load();
        if (!settings.Telemetry.CollectAnonymousData || !settings.Fps.TrackingEnabled)
        {
            return;
        }

        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
        {
            return;
        }

        var pending = _sessions.QueryPendingForUpload(MaxSessionsPerBatch);
        if (pending.Count == 0)
        {
            return;
        }

        var invalid = pending.Where(r => !IsStructurallyValid(r)).Select(r => r.Id).ToList();
        if (invalid.Count > 0)
        {
            Console.Error.WriteLine($"[fps-upload] {invalid.Count} session(s) fail structural bounds, marking as not-retryable");
            _sessions.MarkUploadState(invalid, FpsUploadState.Rejected);
        }

        var uploadable = pending.Where(IsStructurallyValid).ToList();
        if (uploadable.Count == 0)
        {
            return;
        }

        var specs = await _specs.GetAsync(ct).ConfigureAwait(false);
        var ramBytes = SystemProfileService.ParseRamGb(specs.Memory) is int gb ? (long)gb * 1024 * 1024 * 1024 : 0;

        var payload = new FpsUploadPayload
        {
            InstallId = installId,
            ClientVersion = BuildInfo.Version,
            Os = TelemetryPlatform.OsTag(),
            OsVersion = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Hardware = new FpsUploadHardware
            {
                Cpu = specs.Processor,
                Gpu = specs.GraphicsCard,
                RamBytes = ramBytes,
                Motherboard = specs.Motherboard,
            },
            Sessions = uploadable.Select(ToUploadSession).ToList(),
        };

        var result = await _transport.SendAsync(payload, ct).ConfigureAwait(false);
        var ids = uploadable.Select(r => r.Id).ToList();
        switch (result)
        {
            case FpsUploadResult.Success:
                _sessions.MarkUploadState(ids, FpsUploadState.Sent);
                break;
            case FpsUploadResult.Rejected:
                Console.Error.WriteLine($"[fps-upload] batch of {ids.Count} rejected, marking as not-retryable");
                _sessions.MarkUploadState(ids, FpsUploadState.Rejected);
                break;
            case FpsUploadResult.Failed:
                break; // left pending; the next pass retries the whole batch.
        }
    }

    // FpsSessionRecorder legitimately persists DispW/DispH/RefreshHz as 0 when
    // display topology could not be resolved; that never becomes valid on
    // retry, and one such session would otherwise 400 the whole batch.
    private static bool IsStructurallyValid(FpsSessionRecord r) =>
        r.DispW is >= 1 and <= MaxDispDimension &&
        r.DispH is >= 1 and <= MaxDispDimension &&
        r.RefreshHz is >= 1 and <= MaxRefreshHz &&
        r.WinW is >= 1 and <= MaxDispDimension &&
        r.WinH is >= 1 and <= MaxDispDimension &&
        r.FocusedSec <= MaxFocusedSec;

    private static FpsUploadSession ToUploadSession(FpsSessionRecord r) => new()
    {
        Id = r.Id.ToString(),
        GameKey = r.GameKey,
        GameName = r.GameName,
        Store = r.Store,
        FocusedSec = r.FocusedSec,
        ValidSec = r.ValidSec,
        Frames = r.Frames,
        MinFps = r.MinFps,
        MaxFps = r.MaxFps,
        Hist = Array.ConvertAll(r.Hist, h => (int)h),
        HistSchema = 1,
        DispW = r.DispW,
        DispH = r.DispH,
        RefreshHz = r.RefreshHz,
        WinW = r.WinW,
        WinH = r.WinH,
        Fullscreen = r.Fullscreen,
        Capped = r.Capped,
        CapValue = r.CapValue,
    };
}
