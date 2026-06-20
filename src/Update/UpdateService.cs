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

// Flag file written before launching the installer so the helper, on its next
// startup, opens the dashboard once the overlay is ready - replacing the racy
// boot-time IPC approach. Path: %ProgramData%\Nexus\reopen-dashboard.flag

/// <summary>
/// Singleton update engine. Implements IHostedService so it polls on a
/// background timer. Holds mutable status and progress DTOs the routes read.
///
/// Key invariants:
/// - Never blocks startup. All polling + installs are fire-and-forget or
///   background tasks. Every poll failure is swallowed into lastCheckError.
/// - One install at a time (gate via Interlocked + CancellationTokenSource).
/// - UpdateMode "notify": detect only; no background download.
/// - UpdateMode "download": stage a download+verify; installer not launched
///   until the user triggers POST /update/start.
/// - UpdateMode "always": stage a download+verify on detection; apply on the
///   next restart via ApplyPendingOnStartup (never launches mid-session).
/// </summary>
public sealed class UpdateService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(4);

    private static readonly string ReopenFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus",
        "reopen-dashboard.flag");

    private readonly IUpdateSource _source;
    private readonly UpdateDownloader _downloader;
    private readonly IConfigStore _store;
    private readonly FirmwareFlasher _flasher;
#if WINDOWS
    private readonly HelperRegistry _helperRegistry;
#endif

    // Status DTO - read by routes, written only by this service.
    private volatile UpdateStatusResponse _status = new() { CurrentVersion = BuildInfo.Version };
    // Progress DTO - read by routes during an active install.
    private volatile UpdateProgressResponse _progress = new();

    // Set when ApplyPendingOnStartup confirms the version advanced. Cleared
    // after the first GET /update/status read or after 60 seconds.
    private volatile string _justUpdatedTo = "";
    private long _justUpdatedToSetAtTicks;
    private const long JustUpdatedToTimeoutTicks = 60L * TimeSpan.TicksPerSecond;

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
        FirmwareFlasher flasher
#if WINDOWS
        , HelperRegistry helperRegistry
#endif
        )
    {
        _source = source;
        _downloader = downloader;
        _store = store;
        _flasher = flasher;
#if WINDOWS
        _helperRegistry = helperRegistry;
#endif
    }

    /// <summary>Current status snapshot for GET /update/status.</summary>
    public UpdateStatusResponse Status
    {
        get
        {
            var snap = _status;
            var justUpdated = Interlocked.Exchange(ref _justUpdatedTo, "");
            if (justUpdated == "")
            {
                return snap;
            }

            // Ticks were written before the volatile string; the exchange above
            // guarantees _justUpdatedToSetAtTicks is visible.
            var elapsed = DateTime.UtcNow.Ticks - Volatile.Read(ref _justUpdatedToSetAtTicks);
            if (elapsed < JustUpdatedToTimeoutTicks)
            {
                return SnapWithJustUpdatedTo(snap, justUpdated);
            }
            return snap;
        }
    }

    private static UpdateStatusResponse SnapWithJustUpdatedTo(UpdateStatusResponse snap, string justUpdated)
    {
        return new UpdateStatusResponse
        {
            CurrentVersion = snap.CurrentVersion,
            LatestVersion = snap.LatestVersion,
            UpdateAvailable = snap.UpdateAvailable,
            Channel = snap.Channel,
            UpdateMode = snap.UpdateMode,
            ReleaseNotes = snap.ReleaseNotes,
            LastCheckedUnix = snap.LastCheckedUnix,
            LastCheckError = snap.LastCheckError,
            State = snap.State,
            UpdateReady = snap.UpdateReady,
            JustUpdatedTo = justUpdated,
        };
    }

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

        // ApplyPendingOnStartup no longer self-stops; fall through to the poll
        // loop, which the installer's net stop cancels when an install proceeds.
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
    /// Success (version advanced): sets JustUpdatedTo and deletes the marker. Dashboard reopen
    /// is handled by the helper reading the flag file written before the installer was launched.
    /// Pending + always mode: re-verifies and launches the staged installer, then stops the service.
    /// Pending + other modes: no-op (notify/download wait for an explicit user trigger).
    /// Attempted but version did not advance: boot-loop guard; clears marker and sets failed state.
    /// </summary>
    private void ApplyPendingOnStartup(CancellationToken ct)
    {
        var s = _store.Load();

        var marker = StagedInstallMarkerStore.Read();
        if (marker is null)
        {
            return;
        }

        // Current version is at or beyond the marker's target: install succeeded.
        if (!VersionCompare.IsNewer(marker.Version, BuildInfo.Version))
        {
            // Ticks written before the volatile string so any reader that observes
            // the non-empty string sees the already-committed ticks value.
            Volatile.Write(ref _justUpdatedToSetAtTicks, DateTime.UtcNow.Ticks);
            _justUpdatedTo = BuildInfo.Version;
            StagedInstallMarkerStore.Delete();
            return;
        }

        // Marker names a version newer than what is running.
        if (marker.State == StagedInstallMarkerStore.StatePending)
        {
            if (s.Update.UpdateMode == "always")
            {
                // Downloaded and verified but not yet launched. Apply it now.
                ApplyPendingInstall(marker, s, ct);
            }
            // notify/download: staged installer waits for POST /update/start.
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
            UpdateMode = s.Update.UpdateMode,
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

            var reopenDashboard = marker.ReopenDashboard || s.Update.UpdateMode == "always";

            // Flip to "attempted" before launching so a crash here is not retried.
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = marker.Version,
                InstallerPath = marker.InstallerPath,
                Sha256 = marker.Sha256,
                State = StagedInstallMarkerStore.StateAttempted,
                ReopenDashboard = reopenDashboard,
            });

            if (reopenDashboard)
            {
                WriteFlagFile();
            }

            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = marker.Version,
                UpdateAvailable = true,
                Channel = channel,
                UpdateMode = s.Update.UpdateMode,
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

            // Don't self-stop: the installer's `net stop` owns the stop. If the
            // installer can't proceed, the service keeps running the old version
            // instead of being left dead.
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
                UpdateMode = s.Update.UpdateMode,
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
    public (bool started, string reason) StartUpdate(string? requiredVersion, bool reopenAfter = false)
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
            _ = Task.Run(() => RunLaunchStagedAsync(_latestManifest, _stagedInstallerPath, reopenAfter, cts.Token));
        }
        else
        {
            _ = Task.Run(() => RunInstallAsync(_latestManifest, launchAfterVerify: true, reopenAfter, cts.Token));
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

            var mode = snapshot.Update.UpdateMode;
            _status = new UpdateStatusResponse
            {
                CurrentVersion = BuildInfo.Version,
                LatestVersion = manifest?.Version ?? BuildInfo.Version,
                UpdateAvailable = isNewer,
                Channel = channel,
                UpdateMode = mode,
                ReleaseNotes = manifest?.Notes ?? "",
                LastCheckedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                LastCheckError = "",
                State = _installing == 1 ? GetInstallStateString() : (_updateReady ? "ready" : "idle"),
                UpdateReady = _updateReady,
            };

            // "download" stages a download+verify but does not install.
            // "always" downloads, verifies, and installs automatically.
            // "notify" only detects; no background download.
            // Both "download" and "always" auto-stage (download+verify) but never
            // launch mid-session. "always" applies on the next restart via
            // ApplyPendingOnStartup; "download" waits for POST /update/start.
            if (isNewer && (mode is "download" or "always") && manifest is not null && !_updateReady)
            {
                if (Interlocked.CompareExchange(ref _installing, 1, 0) == 0)
                {
                    var cts = new CancellationTokenSource();
                    _installCts = cts;
                    _ = Task.Run(() => RunInstallAsync(manifest, launchAfterVerify: false, reopenAfter: false, cts.Token));
                    Console.Error.WriteLine($"[update] auto-staging download for {manifest.Version} (mode={mode})");
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
                UpdateMode = snapshot.Update.UpdateMode,
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
            UpdateMode = s.Update.UpdateMode,
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
    private async Task RunLaunchStagedAsync(UpdateManifest manifest, string installerPath, bool reopenAfter, CancellationToken ct)
    {
        try
        {
            SetProgress("verifying", 99, "Verifying staged installer...", manifest.Version);

            if (!File.Exists(installerPath))
            {
                // Staged file is gone; fall through to a full re-download.
                _updateReady = false;
                _stagedInstallerPath = null;
                await RunInstallAsync(manifest, launchAfterVerify: true, reopenAfter, ct).ConfigureAwait(false);
                return;
            }

            await UpdateIntegrity.VerifyAsync(installerPath, manifest.Sha256!, ct).ConfigureAwait(false);

#if WINDOWS
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = manifest.Version,
                InstallerPath = installerPath,
                Sha256 = manifest.Sha256 ?? "",
                State = StagedInstallMarkerStore.StateAttempted,
                ReopenDashboard = reopenAfter,
            });

            if (reopenAfter)
            {
                WriteFlagFile();
            }

            try
            {
                await TrayCommands.ShowUpdaterWindowAsync(
                    _helperRegistry,
                    fromVersion: BuildInfo.Version,
                    toVersion: manifest.Version).ConfigureAwait(false);
            }
            catch { /* non-critical; installer proceeds regardless */ }

            var launched = UpdateInstaller.LaunchViaSchtasks(installerPath, manifest.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            // Installer is running detached now; only here signal the UI to expect
            // a restart. A failed launch above never reaches "launching", so the UI
            // shows "failed" rather than waiting to reconnect to a service that
            // never stopped. The installer's `net stop` does the actual stop.
            SetProgress("launching", 100, "Launching installer...", manifest.Version);
            UpdateStatusState("installing");
            SetProgress("installing", 100, "Installing...", manifest.Version);
#else
            throw new PlatformNotSupportedException("OTA install is Windows-only.");
#endif
        }
        catch (OperationCanceledException)
        {
            SetProgressFailed("Install cancelled.", manifest.Version);
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] staged launch failed: {ex.GetType().Name}: {ex.Message}");
            SetProgressFailed($"{ex.GetType().Name}: {ex.Message}", manifest.Version);
            UpdateStatusState("failed");
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
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
    /// is true, also write the marker and launch via schtasks (the installer's
    /// net stop stops the service).
    /// When false, set <c>_updateReady</c> and send the tray notification.
    /// </summary>
    private async Task RunInstallAsync(UpdateManifest manifest, bool launchAfterVerify, bool reopenAfter, CancellationToken ct)
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
            StagedInstallMarkerStore.Write(new StagedInstallMarker
            {
                Version = manifest.Version,
                InstallerPath = installerPath,
                Sha256 = manifest.Sha256 ?? "",
                State = StagedInstallMarkerStore.StateAttempted,
                ReopenDashboard = reopenAfter,
            });

            if (reopenAfter)
            {
                WriteFlagFile();
            }

            try
            {
                await TrayCommands.ShowUpdaterWindowAsync(
                    _helperRegistry,
                    fromVersion: BuildInfo.Version,
                    toVersion: manifest.Version).ConfigureAwait(false);
            }
            catch { /* non-critical; installer proceeds regardless */ }

            var launched = UpdateInstaller.LaunchViaSchtasks(installerPath, manifest.Version);
            if (!launched)
            {
                throw new InvalidOperationException("schtasks /Run did not succeed.");
            }

            // Installer running detached. Only now signal the UI to expect a
            // restart; a failed launch above goes to "failed" instead. The
            // installer's `net stop` does the actual stop - we don't self-stop, so
            // an installer that can't proceed leaves the old version running.
            SetProgress("launching", 100, "Launching installer...", manifest.Version);
            UpdateStatusState("installing");
            SetProgress("installing", 100, "Installing...", manifest.Version);
#else
            // Non-Windows: update not supported; surface a clear error.
            throw new PlatformNotSupportedException("OTA install is Windows-only.");
#endif
        }
        catch (OperationCanceledException)
        {
            SetProgressFailed("Install cancelled.", manifest.Version);
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] install failed: {ex.GetType().Name}: {ex.Message}");
            SetProgressFailed($"{ex.GetType().Name}: {ex.Message}", manifest.Version);
            UpdateStatusState("failed");
#if WINDOWS
            try { _ = TrayCommands.CloseUpdaterWindowAsync(_helperRegistry); } catch { }
#endif
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

    private static void WriteFlagFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(ReopenFlagPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(ReopenFlagPath, "");
            // Grant BUILTIN\Users (S-1-5-32-545) Modify so the user-session
            // helper can delete this LocalSystem-written flag after reopening.
            // Without it the delete fails and the dashboard reopens on every
            // later helper start. icacls is a no-op / throws off Windows (caught).
            var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(ReopenFlagPath);
            psi.ArgumentList.Add("/grant");
            psi.ArgumentList.Add("*S-1-5-32-545:(M)");
            using var icacls = System.Diagnostics.Process.Start(psi);
            icacls?.WaitForExit(5000);
        }
        catch { /* best-effort */ }
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
            UpdateMode = _status.UpdateMode,
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
            UpdateMode = _status.UpdateMode,
            ReleaseNotes = _status.ReleaseNotes,
            LastCheckedUnix = _status.LastCheckedUnix,
            LastCheckError = _status.LastCheckError,
            State = _status.State,
            UpdateReady = ready,
        };
    }
}
