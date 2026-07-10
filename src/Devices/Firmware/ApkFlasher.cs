using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Models.Devices;
using Nexus.Service.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Devices.Firmware;

/// <summary>Delegate for fetching the qshell manifest entry, injectable in tests.</summary>
internal delegate Task<ToolVersion?> ManifestFetcher(CancellationToken ct);

/// <summary>Delegate for downloading the qshell APK and returning its path, injectable in tests.</summary>
internal delegate Task<string?> ApkDownloader(CancellationToken ct);

/// <summary>
/// Manual-flash path for the qshell APK on the connected Q-series panel.
/// Installs qshell alongside the OEM launcher (both coexist), then sets qshell
/// as the default HOME via cmd package set-home-activity.
/// Acquires <see cref="FlashGate"/> so this flash and a concurrent DFU flash
/// are mutually exclusive.
/// </summary>
public sealed class ApkFlasher
{
    private const string QshellManifestBase = "https://assets.hellonexus.com/qseries/qshell";
    private const string QshellManifestUrl = QshellManifestBase + "/latest.json";
    private const string QshellDownloadBase = QshellManifestBase;

    internal const string QshellPackage = "com.hellonexus.qshell";
    private const string QshellComponent = QshellPackage + "/" + QshellPackage + ".MainActivity";

    // adb shell command that switches the default HOME launcher (no reboot needed).
    private const string SetHomeCmd = "cmd package set-home-activity ";
    // Launches the current default HOME so the panel switches immediately after set-home.
    private const string GoHomeCmd = "am start -a android.intent.action.MAIN -c android.intent.category.HOME";
    // Best-effort check: resolves the current default HOME; output must contain qshell package.
    private const string ResolveHomeCmd = "cmd package resolve-activity -a android.intent.action.MAIN -c android.intent.category.HOME";

    /// <summary>DeviceType key used in firmware-status and flash endpoints.</summary>
    internal const string DeviceTypeKey = "qseries-app";

    private static readonly ExternalToolSpec QshellSpec = new(
        ToolId: "qshell",
        Variant: "apk",
        ManifestUrl: QshellManifestUrl,
        DownloadUrlBase: QshellDownloadBase,
        FilePattern: "*.apk",
        Launch: new ToolLaunchOptions());

    // Manifest cache: avoids a remote HTTPS round-trip on every /status poll (60 s TTL).
    private const int ManifestCacheTtlSeconds = 60;
    private ToolVersion? _cachedManifest;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private readonly IAdbDeviceRegistry _deviceRegistry;
    private readonly ManifestFetcher _fetchManifest;
    private readonly ApkDownloader _downloadApk;
    private readonly AdbInstallRunner _installRunner;
    private readonly AdbUninstallRunner? _uninstallRunner;
    private readonly FlashGate _flashGate;

    /// <summary>Production constructor.</summary>
    public ApkFlasher(IAdbDeviceRegistry deviceRegistry, ExternalToolManager toolManager, FlashGate flashGate)
    {
        _deviceRegistry = deviceRegistry;
        _fetchManifest = ct => toolManager.GetLatestAsync(QshellSpec, ct);
        _downloadApk = ct => toolManager.ResolveAsync(QshellSpec, ct);
        _installRunner = AdbHelpers.RunAdbInstall;
        _flashGate = flashGate;
    }

    /// <summary>Test seam: inject all I/O as delegates.</summary>
    internal ApkFlasher(
        IAdbDeviceRegistry deviceRegistry,
        FlashGate flashGate,
        ManifestFetcher manifestFetcher,
        ApkDownloader apkDownloader,
        AdbInstallRunner? installRunner = null,
        AdbUninstallRunner? uninstallRunner = null)
    {
        _deviceRegistry = deviceRegistry;
        _flashGate = flashGate;
        _fetchManifest = manifestFetcher;
        _downloadApk = apkDownloader;
        _installRunner = installRunner ?? AdbHelpers.RunAdbInstall;
        _uninstallRunner = uninstallRunner;
    }

    /// <summary>
    /// Fetch the latest qshell manifest entry, serving a 60-second cache to
    /// avoid a remote round-trip on every /devices/firmware/status poll.
    /// </summary>
    public async Task<ToolVersion?> GetLatestCachedAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _cacheExpiry && _cachedManifest is not null)
        {
            return _cachedManifest;
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _cacheExpiry && _cachedManifest is not null)
            {
                return _cachedManifest;
            }
            _cachedManifest = await _fetchManifest(ct);
            _cacheExpiry = DateTime.UtcNow.AddSeconds(ManifestCacheTtlSeconds);
            return _cachedManifest;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public bool TryStart(string deviceType, string version, out string error)
    {
        if (deviceType != DeviceTypeKey)
        {
            error = "not handled";
            return false;
        }

        var device = _deviceRegistry.TryGet(QshellPackage);
        if (device is null)
        {
            error = "No Q-series panel is connected.";
            return false;
        }

        if (!_flashGate.TryAcquire(out error))
        {
            return false;
        }

        var status = _flashGate.Status;
        status.DeviceType = DeviceTypeKey;
        status.Version = version;
        status.Phase = "preparing";
        status.Percent = 0;
        status.Message = "Preparing...";
        status.Success = false;
        status.Error = "";
        _ = Task.Run(() => RunAsync(device, CancellationToken.None));
        return true;
    }

    private async Task RunAsync(IAdbDeviceTarget device, CancellationToken ct)
    {
        try
        {
            Set("preparing", 5, "Preparing...");

            var dumpsys = await device.ShellAsync("dumpsys package " + QshellPackage, ct);
            var installedVersionCode = AdbHelpers.ParseVersionCode(dumpsys);

            // Invalidate cache so the flash reads a fresh manifest.
            _cacheExpiry = DateTime.MinValue;
            var latest = await _fetchManifest(ct);
            if (latest is null)
            {
                Fail("Could not fetch the qshell manifest.");
                return;
            }

#if !DEV_TOOLS
            if (latest.VersionCode is int pub && installedVersionCode >= pub)
            {
                Fail("The installed qshell version is already up to date.");
                return;
            }
#endif

            Set("downloading", 40, "Downloading APK...");
            var apkPath = await _downloadApk(ct);
            if (apkPath is null)
            {
                Fail("APK download failed.");
                return;
            }

            Set("installing", 70, "Installing on panel...");
            var adbPath = AdbLocator.ResolveAdbPath();
            if (adbPath is null)
            {
                Fail("adb not found.");
                return;
            }

            // Suppress escalation reboots in QSeriesPortWatcher while the install runs.
            device.InstallInProgress = true;
            bool installSuccess;
            string installOutput;
            bool signatureRecovery;
            try
            {
                (installSuccess, installOutput, signatureRecovery) =
                    AdbHelpers.InstallWithSignatureRecovery(adbPath, device.Serial, apkPath, QshellPackage, _installRunner, _uninstallRunner);
                if (signatureRecovery)
                {
                    // Signing cert changed; recovery uninstalled the old build and retried.
                    Set("installing", 73, "Signature changed; reinstalling...");
                    ServiceLog.Warn($"[apk-flash] cert conflict on {device.Serial}; uninstalled {QshellPackage} and retried");
                }
            }
            finally
            {
                device.InstallInProgress = false;
            }

            if (!installSuccess)
            {
                Fail($"Install failed: {installOutput}");
                return;
            }

            // cmd package set-home-activity prints "Success" on API 30+ (empty on
            // some builds); a real failure prints an error (e.g. the APK declares no
            // HOME intent). Treat Success/empty as ok, any other output as failure.
            Set("activating", 78, "Setting as default launcher...");
            var setHomeOut = (await device.ShellAsync(SetHomeCmd + QshellComponent, ct)).Trim();
            if (setHomeOut.Length > 0 && !setHomeOut.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                Fail($"set-home-activity failed: {setHomeOut}");
                return;
            }
            // Switch the panel to the new HOME immediately.
            await device.ShellAsync(GoHomeCmd, ct);

            Set("verifying", 85, "Verifying...");
            var dumpsys2 = await device.ShellAsync("dumpsys package " + QshellPackage, ct);
            // Skipped when the manifest has no versionCode; the install is assumed correct.
            if (latest.VersionCode is int published && AdbHelpers.ParseVersionCode(dumpsys2) != published)
            {
                Fail("Version mismatch after install.");
                return;
            }
            // Best-effort: confirm qshell is the resolved HOME. Not fatal if the check is unreliable on-device.
            var resolveOut = await device.ShellAsync(ResolveHomeCmd, ct);
            if (!resolveOut.Contains(QshellPackage, StringComparison.Ordinal))
            {
                ServiceLog.Warn($"[apk-flash] resolve-activity did not confirm qshell as HOME: {resolveOut.Trim()}");
            }

            _flashGate.Status.Success = true;
            Set("done", 100, $"Updated to {latest.Version}.");
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _flashGate.Release();
        }
    }

    private void Set(string phase, int percent, string message)
    {
        var status = _flashGate.Status;
        status.Phase = phase;
        status.Percent = percent;
        status.Message = message;
    }

    private void Fail(string error)
    {
        var status = _flashGate.Status;
        status.Phase = "failed";
        status.Success = false;
        status.Error = error;
        status.Message = error;
    }
}
