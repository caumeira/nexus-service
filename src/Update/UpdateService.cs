using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Update;
using Nexus.Service.Persistence;
#if WINDOWS
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
#endif

namespace Nexus.Service.Update;

/// <summary>
/// Singleton update engine. Implements IHostedService so it polls on a
/// background timer. Holds mutable status and progress DTOs the routes read.
///
/// Key invariants:
/// - Never blocks startup. All polling + installs are fire-and-forget or
///   background tasks. Every poll failure is swallowed into lastCheckError.
/// - One install at a time (gate via Interlocked + CancellationTokenSource).
/// - AutoUpdateDisabled=false: poller stages a download+verify; the installer
///   is NOT launched until the user triggers POST /update/start (or on the
///   next startup when a staged installer is pending).
/// - AutoUpdateDisabled=true: poller detects only, no background download.
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(4);

    private readonly IUpdateSource _source;
    private readonly UpdateDownloader _downloader;
    private readonly IConfigStore _store;
    private readonly FirmwareFlasher _flasher;
    private readonly IHostApplicationLifetime _lifetime;
#if WINDOWS
    private readonly HelperRegistry _helperRegistry;
#endif

    // Status DTO - read by routes, written only by this service.
    private volatile UpdateStatusResponse _status = new() { CurrentVersion = BuildInfo.Version };
    // Progress DTO - read by routes during an active install.
    private volatile UpdateProgressResponse _progress = new();

    // Gate: 0 = idle, 1 = in progress.
    private int _installing;
    private CancellationTokenSource? _installCts;

    private volatile UpdateManifest? _latestManifest;
    private volatile bool _updateReady;
    private volatile string? _stagedInstallerPath;

    public UpdateService(
        IUpdateSource source,
        UpdateDownloader downloader,
        IConfigStore store,
        FirmwareFlasher flasher,
        IHostApplicationLifetime lifetime
#if WINDOWS
        , HelperRegistry helperRegistry
#endif
        )
    {
        _source = source;
        _downloader = downloader;
        _store = store;
        _flasher = flasher;
        _lifetime = lifetime;
#if WINDOWS
        _helperRegistry = helperRegistry;
#endif
    }

    /// <summary>Current status snapshot for GET /update/status.</summary>
    public UpdateStatusResponse Status => _status;

    /// <summary>Current progress snapshot for GET /update/progress.</summary>
    public UpdateProgressResponse Progress => _progress;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
#if WINDOWS
        // Clean any stale OTA installer tasks from a prior interrupted install.
        try { UpdateInstaller.CleanOrphanedTasks(); } catch { }
#endif

        // Apply or diagnose a staged install marker from a prior run.
        ApplyPendingOnStartup(stoppingToken);

        // If an apply was triggered on startup, ExecuteAsync won't return until
        // StopApplication fires; fall through to the poll loop otherwise.
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // Beat once at startup, then every PollInterval. PeriodicTimer drops
        // drift if a check runs long.
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update] poll iteration failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Applies or diagnoses a staged install marker on startup.
    /// "pending": re-verifies and launches the staged installer, then stops the service.
    /// "attempted": the prior launch did not advance the version; clears the marker and sets failed state.
    /// No-op when AutoUpdateDisabled or no marker exists.
    /// </summary>
    private void ApplyPendingOnStartup(CancellationToken ct)
    {
        var s = _store.Load();
        if (s.Update.AutoUpdateDisabled)
        {
            return;
        }

        var marker = StagedInstallMarkerStore.Read();
        if (marker is null)
        {
            return;
        }

        // Current version is at or beyond the marker's target: install succeeded.
        if (!VersionCompare.IsNewer(marker.Version, BuildInfo.Version))
        {
            StagedInstallMarkerStore.Delete();
            return;
        }

        // Marker names a version newer than what is running.
        if (marker.State == StagedInstallMarkerStore.StatePending)
        {
            // Downloaded and verified but not yet launched. Apply it now.
            ApplyPendingInstall(marker, s, ct);
            return;
        }

        // State is "attempted" (or any unrecognized value): the installer was
        // launched but the version did not advance. Boot-loop guard.
        Console.Error.WriteLine($"[update] staged install of {marker.Version} did not advance version; treating as failed");
        StagedInstallMarkerStore.Delete();

        _status = new UpdateStatusResponse
        {
            CurrentVersion = BuildInfo.Version,
            LatestVersion = marker.Version,
            UpdateAvailable = true,
            Channel = string.IsNullOrEmpty(s.Update.UpdateChannel) ? "production" : s.Update.UpdateChannel,
            AutoUpdateDisabled = s.Update.AutoUpdateDisabled,
            ReleaseNotes = "",
            LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            LastCheckError = $"Install of {marker.Version} did not complete.",
            State = "failed",
            UpdateReady = false,
        };
    }

    private void ApplyPendingInstall(StagedInstallMarker marker, NexusSettings s, CancellationToken ct)
    {
#if WINDOWS
        try
        {
            var channel = string.IsNullOrEmpty(s.Update.UpdateChannel) ? "production" : s.Update.UpdateChannel;
            Console.Error.WriteLine($"[update] applying pending install of {marker.Version} on startup");

            if (!File.Exists(marker.InstallerPath))
            {
                Console.Error.WriteLine($"[update] pending installer not found at {marker.InstallerPath}; skipping auto-apply");
                StagedInstallMarkerStore.Delete();
                return;
            }

            // The marker.Sha256 was sourced from SHA256SUMS (required for the auto-stage
            // path); re-verify before launching to detect staged-file tampering.
            UpdateIntegrity.VerifyAsync(marker.InstallerPath, marker.Sha256, ct).GetAwaiter().GetResult();

            // Flip to "attempted" before launching so a crash here is not retried.
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = marker.Version,
                InstallerPath = marker.InstallerPath,
                Sha256 = marker.Sha256,
                State = StagedInstallMarkerStore.StateAttempted,
            });

            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = marker.Version,
                UpdateAvailable = true,
                Channel = channel,
                AutoUpdateDisabled = s.Update.AutoUpdateDisabled,
                ReleaseNotes = "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = "",
                State = "installing",
                UpdateReady = false,
            };

            var launched = UpdateInstaller.LaunchViaSchtasks(marker.InstallerPath, marker.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            _lifetime.StopApplication();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] startup apply failed: {ex.GetType().Name}: {ex.Message}");
            StagedInstallMarkerStore.Delete();

            var channel = string.IsNullOrEmpty(s.Update.UpdateChannel) ? "production" : s.Update.UpdateChannel;
            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = marker.Version,
                UpdateAvailable = true,
                Channel = channel,
                AutoUpdateDisabled = s.Update.AutoUpdateDisabled,
                ReleaseNotes = "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = $"Auto-apply failed: {ex.GetType().Name}: {ex.Message}",
                State = "failed",
                UpdateReady = false,
            };
        }
#else
        Console.Error.WriteLine($"[update] pending install of {marker.Version} skipped on non-Windows");
        StagedInstallMarkerStore.Delete();
#endif
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

    /// <summary>
    /// Triggers an immediate check (called by POST /update/check). Always returns
    /// a status snapshot; network errors are surfaced in lastCheckError.
    /// </summary>
    public async Task<UpdateStatusResponse> CheckNowAsync(CancellationToken ct)
    {
        await PollAsync(ct).ConfigureAwait(false);
        return _status;
    }

    /// <summary>
    /// Starts a download-verify-install sequence. Returns false with a reason
    /// string when blocked (mid-flash, already installing, not newer, no hash).
    ///
    /// When a staged installer is already ready, skips the download and goes
    /// straight to re-verify + launch.
    /// </summary>
    public (bool started, string reason) StartUpdate(string? requiredVersion)
    {
        if (_flasher.IsFlashing)
        {
            return (false, "A firmware update is in progress.");
        }

        if (_latestManifest is null)
        {
            return (false, "No update manifest available. Run a check first.");
        }

        if (!string.IsNullOrEmpty(requiredVersion) &&
            !string.Equals(requiredVersion, _latestManifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Version mismatch: manifest is {_latestManifest.Version}, requested {requiredVersion}.");
        }

        if (!VersionCompare.IsNewer(_latestManifest.Version, BuildInfo.Version))
        {
            return (false, $"{_latestManifest.Version} is not newer than the running {BuildInfo.Version}.");
        }

        if (string.IsNullOrEmpty(_latestManifest.Sha256))
        {
            return (false, "Cannot install: no SHA-256 hash available for the update.");
        }

        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            return (false, "An install is already in progress.");
        }

        var cts = new CancellationTokenSource();
        _installCts = cts;

        // If we already have a staged installer, use the fast path.
        if (_updateReady && _stagedInstallerPath is not null)
        {
            _ = Task.Run(() => RunLaunchStagedAsync(_latestManifest, _stagedInstallerPath, cts.Token));
        }
        else
        {
            _ = Task.Run(() => RunInstallAsync(_latestManifest, launchAfterVerify: true, cts.Token));
        }

        return (true, "");
    }

    // --- Internal ---

    private async Task PollAsync(CancellationToken ct)
    {
        var s = _store.Load();
        var channel = string.IsNullOrEmpty(s.Update.UpdateChannel)
            ? "production"
            : s.Update.UpdateChannel;

        SetStatusChecking(channel, s);

        try
        {
            var manifest = await _source.GetLatestAsync(channel, ct).ConfigureAwait(false);

            _latestManifest = manifest;

            var snapshot = _store.Load();
            var isNewer = manifest is not null && VersionCompare.IsNewer(manifest.Version, BuildInfo.Version);

            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = manifest?.Version ?? BuildInfo.Version,
                UpdateAvailable = isNewer,
                Channel = channel,
                AutoUpdateDisabled = snapshot.Update.AutoUpdateDisabled,
                ReleaseNotes = manifest?.Notes ?? "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = "",
                State = _installing == 1 ? GetInstallStateString() : (_updateReady ? "ready" : "idle"),
                UpdateReady = _updateReady,
            };

            // When auto-update is on and a newer version is available, start
            // a background download+verify only - do NOT launch or stop yet.
            if (isNewer && !snapshot.Update.AutoUpdateDisabled && manifest is not null && !_updateReady)
            {
                if (Interlocked.CompareExchange(ref _installing, 1, 0) == 0)
                {
                    var cts = new CancellationTokenSource();
                    _installCts = cts;
                    _ = Task.Run(() => RunInstallAsync(manifest, launchAfterVerify: false, cts.Token));
                    Console.Error.WriteLine($"[update] auto-staging download for {manifest.Version}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var snapshot = _store.Load();
            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = _latestManifest?.Version ?? BuildInfo.Version,
                UpdateAvailable = _latestManifest is not null && VersionCompare.IsNewer(_latestManifest.Version, BuildInfo.Version),
                Channel = channel,
                AutoUpdateDisabled = snapshot.Update.AutoUpdateDisabled,
                ReleaseNotes = _latestManifest?.Notes ?? "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = $"{ex.GetType().Name}: {ex.Message}",
                State = _installing == 1 ? GetInstallStateString() : (_updateReady ? "ready" : "idle"),
                UpdateReady = _updateReady,
            };
            Console.Error.WriteLine($"[update] check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetStatusChecking(string channel, NexusSettings s)
    {
        _status = new UpdateStatusResponse
        {
            CurrentVersion = BuildInfo.Version,
            LatestVersion = _latestManifest?.Version ?? BuildInfo.Version,
            UpdateAvailable = _latestManifest is not null && VersionCompare.IsNewer(_latestManifest.Version, BuildInfo.Version),
            Channel = channel,
            AutoUpdateDisabled = s.Update.AutoUpdateDisabled,
            ReleaseNotes = _latestManifest?.Notes ?? "",
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = "",
            State = "checking",
            UpdateReady = _updateReady,
        };
    }

    private string GetInstallStateString()
    {
        return _progress.Phase switch
        {
            "downloading" => "downloading",
            "verifying"   => "verifying",
            "launching"   => "installing",
            "installing"  => "installing",
            "failed"      => "failed",
            _             => "idle",
        };
    }

    /// <summary>
    /// Re-verify the staged installer and launch it. Called when the user
    /// triggers POST /update/start and a staged installer is already ready.
    /// </summary>
    private async Task RunLaunchStagedAsync(UpdateManifest manifest, string installerPath, CancellationToken ct)
    {
        try
        {
            SetProgress("verifying", 99, "Verifying staged installer...", manifest.Version);

            if (!File.Exists(installerPath))
            {
                // Staged file is gone; fall through to a full re-download.
                _updateReady = false;
                _stagedInstallerPath = null;
                await RunInstallAsync(manifest, launchAfterVerify: true, ct).ConfigureAwait(false);
                return;
            }

            await UpdateIntegrity.VerifyAsync(installerPath, manifest.Sha256!, ct).ConfigureAwait(false);

#if WINDOWS
            SetProgress("launching", 100, "Launching installer...", manifest.Version);
            UpdateStatusState("installing");

            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = manifest.Version,
                InstallerPath = installerPath,
                Sha256 = manifest.Sha256 ?? "",
                State = StagedInstallMarkerStore.StateAttempted,
            });

            var launched = UpdateInstaller.LaunchViaSchtasks(installerPath, manifest.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            SetProgress("installing", 100, "Installing...", manifest.Version);
            _lifetime.StopApplication();
#else
            throw new PlatformNotSupportedException("OTA install is Windows-only.");
#endif
        }
        catch (OperationCanceledException)
        {
            SetProgressFailed("Install cancelled.", manifest.Version);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] staged launch failed: {ex.GetType().Name}: {ex.Message}");
            SetProgressFailed($"{ex.GetType().Name}: {ex.Message}", manifest.Version);
            UpdateStatusState("failed");
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    /// <summary>
    /// Download and verify the installer. When <paramref name="launchAfterVerify"/>
    /// is true, also write the marker and launch via schtasks + StopApplication.
    /// When false, set <c>_updateReady</c> and send the tray notification.
    /// </summary>
    private async Task RunInstallAsync(UpdateManifest manifest, bool launchAfterVerify, CancellationToken ct)
    {
        try
        {
            // Phase: downloading
            SetProgress("downloading", 0, $"Downloading {manifest.Version}...", manifest.Version);

            string? installerPath = null;
            var totalSize = manifest.AssetSize > 0 ? manifest.AssetSize : 1;
            var progress = new Progress<long>(bytesReceived =>
            {
                var pct = Math.Min(99, (double)bytesReceived / totalSize * 100.0);
                SetProgress("downloading", pct, $"Downloading {manifest.Version}...", manifest.Version);
            });

            installerPath = await _downloader.DownloadAsync(manifest, progress, ct).ConfigureAwait(false);

            // Phase: verifying
            SetProgress("verifying", 99, "Verifying installer...", manifest.Version);
            await UpdateIntegrity.VerifyAsync(installerPath, manifest.Sha256!, ct).ConfigureAwait(false);

            if (!launchAfterVerify)
            {
                if (!manifest.Sha256IsFromSumsFile)
                {
                    // SHA256SUMS not published for this release. Auto-stage requires the
                    // author-published hash; the asset digest is corruption-only, not authenticity.
                    Console.Error.WriteLine($"[update] skipping auto-stage of {manifest.Version}: SHA256SUMS not available");
                    TryDeleteStagedFile(installerPath);
                    return;
                }

                // Stage only: signal ready, write pending marker, send tray notification.
                _stagedInstallerPath = installerPath;
                _updateReady = true;
                StagedInstallMarkerStore.Write(new StagedInstallMarker
                {
                    Version = manifest.Version,
                    InstallerPath = installerPath,
                    Sha256 = manifest.Sha256 ?? "",
                    State = StagedInstallMarkerStore.StatePending,
                });
                UpdateStatusState("ready");
                UpdateStatusUpdateReady(true);

#if WINDOWS
                try
                {
                    await TrayCommands.UpdateReadyAsync(_helperRegistry, manifest.Version).ConfigureAwait(false);
                }
                catch { /* notification is non-critical */ }
#endif
                Console.Error.WriteLine($"[update] download staged for {manifest.Version}; awaiting user trigger");
                return;
            }

#if WINDOWS
            // Phase: launching (point of no return - installer is verified)
            SetProgress("launching", 100, "Launching installer...", manifest.Version);
            UpdateStatusState("installing");

            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = manifest.Version,
                InstallerPath = installerPath,
                Sha256 = manifest.Sha256 ?? "",
                State = StagedInstallMarkerStore.StateAttempted,
            });

            var launched = UpdateInstaller.LaunchViaSchtasks(installerPath, manifest.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            SetProgress("installing", 100, "Installing...", manifest.Version);
            _lifetime.StopApplication();
#else
            // Non-Windows: update not supported; surface a clear error.
            throw new PlatformNotSupportedException("OTA install is Windows-only.");
#endif
        }
        catch (OperationCanceledException)
        {
            SetProgressFailed("Install cancelled.", manifest.Version);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] install failed: {ex.GetType().Name}: {ex.Message}");
            SetProgressFailed($"{ex.GetType().Name}: {ex.Message}", manifest.Version);
            UpdateStatusState("failed");
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private static void TryDeleteStagedFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private void SetProgress(string phase, double percent, string message, string version)
    {
        _progress = new UpdateProgressResponse
        {
            Active = true,
            Phase = phase,
            Percent = percent,
            Message = message,
            Version = version,
            Success = false,
            Error = "",
        };
    }

    private void SetProgressFailed(string error, string version)
    {
        _progress = new UpdateProgressResponse
        {
            Active = false,
            Phase = "failed",
            Percent = 0,
            Message = error,
            Version = version,
            Success = false,
            Error = error,
        };
    }

    private void UpdateStatusState(string state)
    {
        _status = new UpdateStatusResponse
        {
            CurrentVersion = _status.CurrentVersion,
            LatestVersion = _status.LatestVersion,
            UpdateAvailable = _status.UpdateAvailable,
            Channel = _status.Channel,
            AutoUpdateDisabled = _status.AutoUpdateDisabled,
            ReleaseNotes = _status.ReleaseNotes,
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = _status.LastCheckError,
            State = state,
            UpdateReady = _updateReady,
        };
    }

    private void UpdateStatusUpdateReady(bool ready)
    {
        _status = new UpdateStatusResponse
        {
            CurrentVersion = _status.CurrentVersion,
            LatestVersion = _status.LatestVersion,
            UpdateAvailable = _status.UpdateAvailable,
            Channel = _status.Channel,
            AutoUpdateDisabled = _status.AutoUpdateDisabled,
            ReleaseNotes = _status.ReleaseNotes,
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = _status.LastCheckError,
            State = _status.State,
            UpdateReady = ready,
        };
    }
}
