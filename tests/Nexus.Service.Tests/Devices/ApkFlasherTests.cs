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

public class ApkFlasherTests
{
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
    private const string OemPackage = "com.companyname.thiccapp";
    private const string OemComponent = OemPackage + "/" + OemPackage + ".MainActivity";

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

    private static async Task WaitForPhaseAsync(FlashStatusDto status, string phase, int timeoutMs = 5000)
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
    public async Task RevertPanelHome_IssuesOemSetHomeActivity()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var device = new FakeDevice("emulator-5554", QshellPackage);
        registry.Register(device);

        var flasher = MakeFlasher(registry, gate);

        var result = await flasher.RevertPanelHomeAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(device.IssuedCommands,
            c => c.Contains("set-home-activity", StringComparison.Ordinal)
              && c.Contains(OemComponent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevertPanelHome_ReturnsErrorWhenNoDevice()
    {
        var registry = new FakeRegistry();
        var gate = new FlashGate();
        var flasher = MakeFlasher(registry, gate);

        var result = await flasher.RevertPanelHomeAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("connected", result, StringComparison.OrdinalIgnoreCase);
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
}
