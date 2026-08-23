using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Models.Devices;
using Xunit;

namespace Nexus.Service.Tests.Devices;

public class ApkFlasherTests : IDisposable
{
    // The readiness poll paces every factory-reset test against a fake device;
    // at the production 2 s it accounted for the whole class runtime.
    private readonly TimeSpan _pollInterval = ApkFlasher.DeviceReturnPollInterval;

    public ApkFlasherTests() => ApkFlasher.DeviceReturnPollInterval = TimeSpan.FromMilliseconds(1);
    public void Dispose() => ApkFlasher.DeviceReturnPollInterval = _pollInterval;

    // ── Test doubles ─────────────────────────────────────────────────────────────

    private sealed class FakeRegistry : IAdbDeviceRegistry
    {
        private readonly ConcurrentDictionary<string, IAdbDeviceTarget> _map = new(StringComparer.Ordinal);
        public void Register(IAdbDeviceTarget device) => _map[device.Package] = device;
        public void Unregister(string package) => _map.TryRemove(package, out _);
        public IAdbDeviceTarget? TryGet(string package)
            => _map.TryGetValue(package, out var t) ? t : null;
    }

    private sealed class FakeDevice : IAdbDeviceTarget
    {
        // Records every shell command issued in order.
        public List<string> IssuedCommands { get; } = new();

        private readonly Func<string, string> _shell;
        public string Serial { get; }
        public string Package { get; }
        public bool InstallInProgress { get; set; }

        public FakeDevice(string serial, string package, Func<string, string>? shell = null)
        {
            Serial = serial;
            Package = package;
            _shell = shell ?? (_ => string.Empty);
        }

        public Task<string> ShellAsync(string command, CancellationToken ct)
        {
            IssuedCommands.Add(command);
            return Task.FromResult(_shell(command));
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private const string QshellPackage = "com.hellonexus.qshell";
    private const string QshellComponent = QshellPackage + "/" + QshellPackage + ".MainActivity";

    /// <summary>
    /// Default dumpsys response factory: first call returns old versionCode,
    /// all subsequent calls return the new versionCode (post-install).
    /// Handles set-home-activity and resolve-activity commands with empty/success output.
    /// </summary>
    private static FakeDevice MakeDevice(
        string serial = "emulator-5554",
        int oldCode = 5,
        int newCode = 20,
        string? setHomeResponse = null,
        string resolveHomeResponse = QshellPackage)
    {
        var callCount = 0;
        return new FakeDevice(serial, QshellPackage, cmd =>
        {
            if (cmd.StartsWith("dumpsys package", StringComparison.Ordinal))
            {
                callCount++;
                return callCount == 1
                    ? $"  versionCode={oldCode} targetSdk=33\n  versionName=1.0.0\n"
                    : $"  versionCode={newCode} targetSdk=33\n  versionName=2.0.0\n";
            }
            if (cmd.StartsWith("cmd package set-home-activity", StringComparison.Ordinal))
            {
                return setHomeResponse ?? string.Empty;
            }
            if (cmd.StartsWith("cmd package resolve-activity", StringComparison.Ordinal))
            {
                return resolveHomeResponse;
            }
            // Post-wipe readiness probe: echo it back so the wait resolves at once.
            if (cmd.StartsWith("echo ", StringComparison.Ordinal))
            {
                return cmd.Substring(5);
            }
            // go-home and any other commands succeed silently.
            return string.Empty;
        });
    }

    private static ApkFlasher MakeFlasher(
        FakeRegistry registry,
        FlashGate gate,
        ManifestFetcher? manifest = null,
        ApkDownloader? downloader = null,
        AdbInstallRunner? installRunner = null,
        AdbUninstallRunner? uninstallRunner = null)
    {
        manifest ??= _ => Task.FromResult<ToolVersion?>(
            new ToolVersion { Version = "2.0.0", VersionCode = 20, FileName = "qshell-2.0.0.apk" });
        downloader ??= _ => Task.FromResult<string?>("/fake/qshell.apk");
        return new ApkFlasher(
            deviceRegistry: registry,
            flashGate: gate,
            manifestFetcher: manifest,
            apkDownloader: downloader,
            installRunner: installRunner,
            uninstallRunner: uninstallRunner);
    }

    // Polls until the phase lands, so this only bounds a genuine stall and has
    // to outlast a loaded parallel suite rather than a quiet machine.
    private static async Task WaitForPhaseAsync(FlashStatusDto status, string phase, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (status.Phase == phase)
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Phase '{phase}' not reached within {timeoutMs}ms; current: '{status.Phase}'");
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallsWhenNewerVersionAvailable()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var runnerCalled = 0;
        var device = MakeDevice(oldCode: 10, newCode: 20);
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { runnerCalled++; return (true, "Success"); });

        var started = flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out var error);
        Assert.True(started, error);

        await WaitForPhaseAsync(gate.Status, "done");
        Assert.True(gate.Status.Success);
        Assert.Equal(1, runnerCalled);
    }

    [Fact]
    public async Task BlocksDowngradeInReleaseMode()
    {
        // versionCode=20 installed, manifest says 10 => no-downgrade gate fires (non-DEV_TOOLS)
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var runnerCalled = 0;
        var flasher = MakeFlasher(registry, gate,
            manifest: _ => Task.FromResult<ToolVersion?>(
                new ToolVersion { Version = "1.0.0", VersionCode = 10, FileName = "qshell-1.0.0.apk" }),
            installRunner: (adb, serial, apk) => { runnerCalled++; return (true, "Success"); });

        var started = flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out var error);
        Assert.True(started, error);

#if !DEV_TOOLS
        await WaitForPhaseAsync(gate.Status, "failed");
        Assert.Equal(0, runnerCalled);
        Assert.False(gate.Status.Success);
#else
        await WaitForPhaseAsync(gate.Status, "done");
        Assert.Equal(1, runnerCalled);
#endif
    }

    [Fact]
    public async Task ProgressesThroughAllPhases()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5, newCode: 20);
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => (true, "Success"));

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.Equal("done", gate.Status.Phase);
        Assert.True(gate.Status.Success);
        Assert.Equal(100, gate.Status.Percent);
    }

    [Fact]
    public async Task ReturnsFailureOnAdbError()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5);
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => (false, "Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]"));

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "failed");

        Assert.False(gate.Status.Success);
        Assert.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE", gate.Status.Error);
    }

    [Fact]
    public void NoopWhenNoDevice()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();

        var runnerCalled = 0;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { runnerCalled++; return (true, "Success"); });

        var started = flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out var error);

        Assert.False(started);
        Assert.Equal(0, runnerCalled);
        Assert.Contains("connected", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MutualExclusion_SecondApkFlashBlockedWhileFirstRunning()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5);
        registry.Register(device);

        // Stall the downloader so the flash stays in-flight.
        var stall = new TaskCompletionSource<string?>();
        var flasher = MakeFlasher(registry, gate,
            downloader: _ => stall.Task,
            installRunner: (adb, serial, apk) => (true, "Success"));

        var first = flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        Assert.True(first);

        var second = flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out var error2);
        Assert.False(second);
        Assert.Contains("in progress", error2, StringComparison.OrdinalIgnoreCase);
        Assert.True(gate.IsFlashing);

        stall.SetResult(null);
    }

    [Fact]
    public void MutualExclusion_CoolerFlashBlockedWhileApkRunning()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5);
        registry.Register(device);

        var stall = new TaskCompletionSource<string?>();
        var flasher = MakeFlasher(registry, gate,
            downloader: _ => stall.Task,
            installRunner: (adb, serial, apk) => (true, "Success"));

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);

        // FirmwareFlasher.IsFlashing delegates to gate.IsFlashing.
        Assert.True(gate.IsFlashing);

        stall.SetResult(null);
    }

    [Fact]
    public async Task InstallInProgress_SetDuringInstallAndClearedAfter()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5, newCode: 20);
        registry.Register(device);

        bool installInProgressDuringRun = false;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) =>
            {
                installInProgressDuringRun = device.InstallInProgress;
                return (true, "Success");
            });

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(installInProgressDuringRun);
        Assert.False(device.InstallInProgress);
    }

    [Fact]
    public async Task SetHomeActivity_IssuedWithQshellComponentAfterInstall()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5, newCode: 20);
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => (true, "Success"));

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "done");

        // set-home-activity must target the qshell component.
        Assert.Contains(device.IssuedCommands,
            c => c.Contains("set-home-activity", StringComparison.Ordinal)
              && c.Contains(QshellComponent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetHomeActivity_FailureMarksFlashFailed()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        // set-home-activity returns non-empty output -> failure.
        var device = MakeDevice(oldCode: 5, newCode: 20, setHomeResponse: "Error: unknown component");
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => (true, "Success"));

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "failed");

        Assert.False(gate.Status.Success);
        Assert.Contains("set-home-activity failed", gate.Status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetHomeActivity_SuccessOutput_FlashSucceeds()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        // API 30+ prints "Success" on set-home-activity; that is not a failure.
        var device = MakeDevice(oldCode: 5, newCode: 20, setHomeResponse: "Success");
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => (true, "Success"));

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(gate.Status.Success);
    }

    [Fact]
    public async Task SignatureConflict_UninstallsAndRetries_FlashSucceeds()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5, newCode: 20);
        registry.Register(device);

        var installCallCount = 0;
        var uninstallCalledForPackage = (string?)null;

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) =>
            {
                installCallCount++;
                // First call: simulate cert conflict; second call (after uninstall): succeed.
                return installCallCount == 1
                    ? (false, "adb: failed to install: Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]")
                    : (true, "Success");
            },
            uninstallRunner: (adb, serial, pkg) =>
            {
                uninstallCalledForPackage = pkg;
                return (true, "Success");
            });

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(gate.Status.Success);
        Assert.Equal(2, installCallCount);
        Assert.Equal(QshellPackage, uninstallCalledForPackage);
    }

    [Fact]
    public async Task NonSignatureFailure_DoesNotTriggerUninstall()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 5);
        registry.Register(device);

        var uninstallCalled = false;

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) =>
                (false, "adb: failed to install: Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]"),
            uninstallRunner: (adb, serial, pkg) =>
            {
                uninstallCalled = true;
                return (true, "Success");
            });

        flasher.TryStart(ApkFlasher.DeviceTypeKey, "", out _);
        await WaitForPhaseAsync(gate.Status, "failed");

        Assert.False(gate.Status.Success);
        Assert.False(uninstallCalled);
    }

    // ── Factory reset ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FactoryReset_UninstallsBeforeReinstalling()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var order = new List<string>();
        string? uninstalledPackage = null;

        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { order.Add("install"); return (true, "Success"); },
            uninstallRunner: (adb, serial, pkg) =>
            {
                order.Add("uninstall");
                uninstalledPackage = pkg;
                return (true, "Success");
            });

        var started = flasher.TryStartFactoryReset(out var error);
        Assert.True(started, error);

        await WaitForPhaseAsync(gate.Status, "done");
        Assert.True(gate.Status.Success);
        // The wipe must precede the reinstall, or the panel keeps its old data.
        Assert.Equal(new[] { "uninstall", "install" }, order);
        Assert.Equal(QshellPackage, uninstalledPackage);
    }

    [Fact]
    public async Task FactoryReset_ProceedsWhenInstalledVersionIsCurrent()
    {
        // The manual-flash path refuses a same-version install; a factory reset
        // reinstalls the current build on purpose and must not hit that gate.
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var installCalled = 0;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { installCalled++; return (true, "Success"); },
            uninstallRunner: (adb, serial, pkg) => (true, "Success"));

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(gate.Status.Success);
        Assert.Equal(1, installCalled);
    }

    [Theory]
    [InlineData("Failure [DELETE_FAILED_INTERNAL_ERROR]\nUnknown package: com.hellonexus.qshell")]
    [InlineData("Failure [not installed for 0]")]
    public async Task FactoryReset_TreatsAlreadyAbsentPackageAsWiped(string uninstallOutput)
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var installCalled = 0;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { installCalled++; return (true, "Success"); },
            uninstallRunner: (adb, serial, pkg) => (false, uninstallOutput));

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(gate.Status.Success);
        Assert.Equal(1, installCalled);
    }

    [Fact]
    public async Task FactoryReset_RealUninstallFailureAbortsBeforeInstalling()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var installCalled = 0;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { installCalled++; return (true, "Success"); },
            uninstallRunner: (adb, serial, pkg) => (false, "Failure [DELETE_FAILED_DEVICE_POLICY_MANAGER]"));

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "failed");

        Assert.False(gate.Status.Success);
        Assert.Contains("Uninstall failed", gate.Status.Error, StringComparison.Ordinal);
        Assert.Equal(0, installCalled);
    }

    [Fact]
    public async Task FactoryReset_HoldsInstallInProgressAcrossUninstallAndInstall()
    {
        // A watcher tick between the uninstall and the install would find no
        // launcher on the panel; InstallInProgress is what suppresses it.
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        bool duringUninstall = false, duringInstall = false;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { duringInstall = device.InstallInProgress; return (true, "Success"); },
            uninstallRunner: (adb, serial, pkg) => { duringUninstall = device.InstallInProgress; return (true, "Success"); });

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(duringUninstall);
        Assert.True(duringInstall);
        Assert.False(device.InstallInProgress);
    }

    [Fact]
    public async Task FactoryReset_WaitsForThePanelToReturnBeforeInstalling()
    {
        // Uninstalling the pinned HOME activity drops the panel off adb while
        // Android re-resolves a launcher; installing into that window fails with
        // "device not found" (bench-hit on the Q60).
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var shellCallsAfterUninstall = 0;
        var uninstalled = false;
        var readyAfter = 3;
        var device = new FakeDevice("emulator-5554", QshellPackage, cmd =>
        {
            if (cmd.StartsWith("dumpsys package", StringComparison.Ordinal))
                return "  versionCode=20 targetSdk=33\n  versionName=2.0.0\n";
            if (cmd.StartsWith("echo ", StringComparison.Ordinal))
            {
                if (!uninstalled) return cmd.Substring(5);
                // Absent device: ShellAsync yields empty until it re-enumerates.
                shellCallsAfterUninstall++;
                return shellCallsAfterUninstall >= readyAfter ? cmd.Substring(5) : string.Empty;
            }
            if (cmd.StartsWith("cmd package resolve-activity", StringComparison.Ordinal)) return QshellPackage;
            return string.Empty;
        });
        registry.Register(device);

        var installedWhileGone = false;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) =>
            {
                if (shellCallsAfterUninstall < readyAfter) installedWhileGone = true;
                return (true, "Success");
            },
            uninstallRunner: (adb, serial, pkg) => { uninstalled = true; return (true, "Success"); });

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(gate.Status.Success);
        Assert.False(installedWhileGone);
    }

    [Theory]
    [InlineData("adb.exe: device '0123456789ABCDEF' not found")]
    [InlineData("error: device offline")]
    [InlineData("error: no devices/emulators found")]
    public void IsDeviceMissing_recognises_adb_absence_outputs(string output)
    {
        Assert.True(ApkFlasher.IsDeviceMissing(output));
    }

    [Theory]
    [InlineData("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]")]
    [InlineData("Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE]")]
    public void IsDeviceMissing_ignores_real_install_failures(string output)
    {
        Assert.False(ApkFlasher.IsDeviceMissing(output));
    }

    [Fact]
    public async Task FactoryReset_RetriesInstallWhenThePanelReEnumeratesMidInstall()
    {
        // The panel re-enumerates several times after the wipe (transport id
        // 2 -> 3 -> 4 on the Q60), so an install can lose a transport that was
        // live when the readiness wait returned. That is retryable, not fatal.
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var attempts = 0;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) =>
            {
                attempts++;
                return attempts < 3
                    ? (false, "adb.exe: device '0123456789ABCDEF' not found")
                    : (true, "Success");
            },
            uninstallRunner: (adb, serial, pkg) => (true, "Success"));

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "done");

        Assert.True(gate.Status.Success);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task FactoryReset_DoesNotAddDeviceLossRetriesToARealInstallFailure()
    {
        // A storage failure is not a lost transport. InstallWithSignatureRecovery
        // already spends its own 3 transient attempts on it; the device-loss loop
        // must not multiply those into another round of waiting.
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = MakeDevice(oldCode: 20, newCode: 20);
        registry.Register(device);

        var attempts = 0;
        var flasher = MakeFlasher(registry, gate,
            installRunner: (adb, serial, apk) => { attempts++; return (false, "Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]"); },
            uninstallRunner: (adb, serial, pkg) => (true, "Success"));

        flasher.TryStartFactoryReset(out _);
        await WaitForPhaseAsync(gate.Status, "failed");

        Assert.False(gate.Status.Success);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void FactoryReset_NoopWhenNoDevice()
    {
        var flasher = MakeFlasher(new FakeRegistry(), new FlashGate());
        Assert.False(flasher.TryStartFactoryReset(out var error));
        Assert.Contains("No Q-series panel", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryReset_BlockedWhileAnotherFlashRuns()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        registry.Register(MakeDevice());
        Assert.True(gate.TryAcquire(out _));

        var flasher = MakeFlasher(registry, gate);
        Assert.False(flasher.TryStartFactoryReset(out _));
    }
}
