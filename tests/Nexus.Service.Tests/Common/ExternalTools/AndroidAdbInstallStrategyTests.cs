using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

public class AndroidAdbInstallStrategyTests
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
        private readonly Func<string, string> _shell;
        public FakeDevice(string serial, string package, Func<string, string>? shell = null)
        {
            Serial = serial;
            Package = package;
            _shell = shell ?? (_ => string.Empty);
        }
        public string Serial { get; }
        public string Package { get; }
        public bool InstallInProgress { get; set; }
        public Task<string> ShellAsync(string command, CancellationToken ct)
            => Task.FromResult(_shell(command));
    }

    private sealed class FakeResolver : IToolResolver
    {
        public string? Path { get; set; } = "/fake/app.apk";
        /// <summary>versionCode to return from GetLatestAsync; null means no manifest entry.</summary>
        public int? PublishedVersionCode { get; set; }

        public Task<string?> ResolveAsync(ExternalToolSpec spec, CancellationToken ct = default)
            => Task.FromResult(Path);

        public Task<ToolVersion?> GetLatestAsync(ExternalToolSpec spec, CancellationToken ct = default)
        {
            if (PublishedVersionCode is null)
            {
                return Task.FromResult<ToolVersion?>(null);
            }
            return Task.FromResult<ToolVersion?>(new ToolVersion
            {
                Version = "1.0",
                VersionCode = PublishedVersionCode,
            });
        }
    }

    private static ExternalToolSpec MakeSpec(string package = "com.test.app")
        => new(
            ToolId: "test-apk",
            Variant: "v1",
            ManifestUrl: "https://example.com/latest.json",
            DownloadUrlBase: "https://example.com",
            FilePattern: "*.apk",
            Launch: new ToolLaunchOptions(),
            Package: package);

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallsWhenPackageAbsent()
    {
        // dumpsys returns no versionCode (package not installed)
        var installCallCount = 0;
        var registry = new FakeRegistry();
        var device = new FakeDevice("emulator-5554", "com.test.app", _ => "Status: uninstalled");
        registry.Register(device);
        AdbInstallRunner runner = (adb, serial, apk) =>
        {
            installCallCount++;
            return (true, "Success");
        };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");
        var resolver = new FakeResolver();

        await strategy.LaunchAsync(MakeSpec(), resolver, CancellationToken.None);

        Assert.Equal(1, installCallCount);
        Assert.Equal(ToolStatus.Running, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task SkipsWhenInstalledVersionMatchesPublished()
    {
        var installCallCount = 0;
        var registry = new FakeRegistry();
        // installed = 10, published (from manifest) = 10 => skip
        var device = new FakeDevice("emulator-5554", "com.test.app",
            _ => "  versionCode=10\n  longVersionCode=10\n");
        registry.Register(device);
        AdbInstallRunner runner = (adb, serial, apk) =>
        {
            installCallCount++;
            return (true, "Success");
        };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");
        var resolver = new FakeResolver { PublishedVersionCode = 10 };

        await strategy.LaunchAsync(MakeSpec(), resolver, CancellationToken.None);

        Assert.Equal(0, installCallCount);
        Assert.Equal(ToolStatus.Running, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task SkipsWhenInstalledVersionNewerThanPublished()
    {
        var installCallCount = 0;
        var registry = new FakeRegistry();
        // installed = 15, published (from manifest) = 10 => no downgrade
        var device = new FakeDevice("emulator-5554", "com.test.app",
            _ => "  versionCode=15\n");
        registry.Register(device);
        AdbInstallRunner runner = (adb, serial, apk) =>
        {
            installCallCount++;
            return (true, "Success");
        };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");
        var resolver = new FakeResolver { PublishedVersionCode = 10 };

        await strategy.LaunchAsync(MakeSpec(), resolver, CancellationToken.None);

        Assert.Equal(0, installCallCount);
        Assert.Equal(ToolStatus.Running, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task InstallsWhenInstalledVersionOlderThanPublished()
    {
        var installCallCount = 0;
        var registry = new FakeRegistry();
        // installed = 5, published (from manifest) = 10 => install
        var device = new FakeDevice("emulator-5554", "com.test.app",
            _ => "  versionCode=5\n");
        registry.Register(device);
        AdbInstallRunner runner = (adb, serial, apk) =>
        {
            installCallCount++;
            return (true, "Success");
        };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");
        var resolver = new FakeResolver { PublishedVersionCode = 10 };

        await strategy.LaunchAsync(MakeSpec(), resolver, CancellationToken.None);

        Assert.Equal(1, installCallCount);
        Assert.Equal(ToolStatus.Running, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task ParsesSuccessFromAdbOutput()
    {
        var registry = new FakeRegistry();
        var device = new FakeDevice("emulator-5554", "com.test.app", _ => string.Empty);
        registry.Register(device);
        AdbInstallRunner runner = (adb, serial, apk) => (true, "Performing Streamed Install\nSuccess");
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");

        await strategy.LaunchAsync(MakeSpec(), new FakeResolver(), CancellationToken.None);

        Assert.Equal(ToolStatus.Running, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task ParsesFailureFromAdbOutput()
    {
        var registry = new FakeRegistry();
        var device = new FakeDevice("emulator-5554", "com.test.app", _ => string.Empty);
        registry.Register(device);
        AdbInstallRunner runner = (adb, serial, apk) =>
            (false, "Performing Streamed Install\nFailure [INSTALL_FAILED_VERSION_DOWNGRADE]");
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");

        await strategy.LaunchAsync(MakeSpec(), new FakeResolver(), CancellationToken.None);

        Assert.Equal(ToolStatus.Failed, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task VersionCodeRegexHandlesLongVersionCode()
    {
        // dumpsys output with both fields; the first versionCode match (value 49) must be used.
        const string dumpsysOutput =
            "    versionCode=49 targetSdk=33\n" +
            "    longVersionCode=49\n";

        var registry = new FakeRegistry();
        var device = new FakeDevice("emulator-5554", "com.test.app", _ => dumpsysOutput);
        registry.Register(device);
        // published = 49, installed = 49 => skip; installCount == 0
        var installCount = 0;
        AdbInstallRunner runner = (adb, serial, apk) => { installCount++; return (true, "Success"); };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");
        // installed = 49, published (from manifest) = 49 => skip
        var resolver = new FakeResolver { PublishedVersionCode = 49 };

        await strategy.LaunchAsync(MakeSpec(), resolver, CancellationToken.None);

        Assert.Equal(0, installCount); // already at published version; no install
    }

    [Fact]
    public async Task NoopWhenNoDeviceRegistered()
    {
        var installCount = 0;
        var registry = new FakeRegistry(); // empty
        AdbInstallRunner runner = (adb, serial, apk) => { installCount++; return (true, "Success"); };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: true, runner: runner, adbPath: "adb");

        await strategy.LaunchAsync(MakeSpec(), new FakeResolver(), CancellationToken.None);

        Assert.Equal(0, installCount);
        Assert.Equal(ToolStatus.NotRunning, strategy.GetStatus("test-apk"));
    }

    [Fact]
    public async Task GateOffMeansNoInstall()
    {
        var installCount = 0;
        var registry = new FakeRegistry();
        var device = new FakeDevice("emulator-5554", "com.test.app", _ => string.Empty);
        registry.Register(device);
        // installEnabled: false => gate is off
        AdbInstallRunner runner = (adb, serial, apk) => { installCount++; return (true, "Success"); };
        var strategy = new AndroidAdbInstallStrategy(registry, installEnabled: false, runner: runner, adbPath: "adb");

        await strategy.LaunchAsync(MakeSpec(), new FakeResolver(), CancellationToken.None);

        Assert.Equal(0, installCount);
        Assert.Equal(ToolStatus.NotRunning, strategy.GetStatus("test-apk"));
    }
}
