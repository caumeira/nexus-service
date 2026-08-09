using System;
using System.Collections.Generic;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class DiagnosticsHealthModelTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly SmartSnapshot EmptySmart = new() { Supported = true, Drives = Array.Empty<SmartDriveInfo>() };
    private static readonly CoolingStallSnapshot EmptyCooling = new(true, Array.Empty<CoolingStallDevice>());
    private static readonly PnpProblemSnapshot EmptyPnp = new(true, Array.Empty<PnpProblemDevice>());

    private static DiagnosticsHealthResponse Compute(
        SmartSnapshot? smart = null,
        CoolingStallSnapshot? cooling = null,
        GpuHealthSnapshot? gpu = null,
        IReadOnlyDictionary<string, int>? counts30d = null,
        MemoryTestResult? lastMemoryTest = null,
        PnpProblemSnapshot? pnp = null,
        IReadOnlyList<string>? knownGpuModels = null,
        IReadOnlyList<TemperatureEpisode>? tempEpisodes = null,
        DateTime? generatedAtUtc = null,
        DiagnosticsSettings? diagnostics = null)
    {
        return DiagnosticsHealthModel.Compute(
            smart: smart ?? EmptySmart,
            cooling: cooling ?? EmptyCooling,
            gpu: gpu ?? GpuHealthSnapshot.Unsupported,
            counts30d: counts30d ?? new Dictionary<string, int>(),
            lastMemoryTest: lastMemoryTest,
            pnp: pnp ?? EmptyPnp,
            knownGpuModels: knownGpuModels ?? Array.Empty<string>(),
            windowsSupported: true,
            generatedAtUtc: generatedAtUtc ?? T0,
            tempEpisodes: tempEpisodes,
            diagnostics: diagnostics);
    }

    [Fact]
    public void StorageDriveAct_PropagatesToComponentAndOverall()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:ABC123",
                    Name = "Test SSD",
                    Status = "warning",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("smart.reallocated", ReasonSeverity.Act, "5 reallocated sectors", "detail"),
                    },
                },
            },
        };

        var result = Compute(smart: smart);

        var storage = Assert.Single(result.Components, c => c.Kind == "storage");
        Assert.Equal(HealthStatuses.Act, storage.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(25)]
    public void GpuTdrAndDriverErrorCounts_NeverAffectStatus(int count)
    {
        var gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
        {
            new("Test GPU", "1.0", 50, 100, new GpuThrottleInfo(Array.Empty<string>(), null, null, null, null)),
        });
        var counts = new Dictionary<string, int>
        {
            [DiagnosticEventCatalog.SourceTdr] = count,
            [DiagnosticEventCatalog.SourceGpuDriver] = count,
        };

        var result = Compute(gpu: gpu, counts30d: counts);

        var gpuComponent = Assert.Single(result.Components, c => c.Kind == "gpu");
        Assert.Equal(HealthStatuses.Ok, gpuComponent.Status);
        Assert.Empty(gpuComponent.Reasons);
    }

    [Fact]
    public void DirtyShutdownsBugchecksAndWhea_NeverAffectStatus_RegardlessOfCount()
    {
        var counts = new Dictionary<string, int>
        {
            [DiagnosticEventCatalog.SourceDirtyShutdown] = 1000,
            [DiagnosticEventCatalog.SourceBugcheck] = 1000,
            [DiagnosticEventCatalog.SourceWhea] = 1000,
        };

        var result = Compute(counts30d: counts);

        var system = Assert.Single(result.Components, c => c.Kind == "system");
        Assert.Equal(HealthStatuses.Ok, system.Status);
        Assert.Empty(system.Reasons);

        var memory = Assert.Single(result.Components, c => c.Kind == "memory");
        Assert.Equal(HealthStatuses.Ok, memory.Status);
        Assert.Empty(memory.Reasons);

        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void OverallStatus_IsWorstAcrossAllComponents()
    {
        // pnp problems -> system component at "watch"; a bad storage drive -> "act".
        // Overall must reflect the worst of the two, not the last one computed.
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:XYZ",
                    Name = "Failing Drive",
                    Status = "bad",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("nvme.criticalWarning", ReasonSeverity.Act, "critical", "detail"),
                    },
                },
            },
        };
        var pnp = new PnpProblemSnapshot(true, new List<PnpProblemDevice> { new("Bad Device", "PCI\\1234", 43, "CM_PROB_FAILED_POST_START") });

        var result = Compute(smart: smart, pnp: pnp);

        var system = Assert.Single(result.Components, c => c.Kind == "system");
        Assert.Equal(HealthStatuses.Watch, system.Status);
        var storage = Assert.Single(result.Components, c => c.Kind == "storage");
        Assert.Equal(HealthStatuses.Act, storage.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void Cooling_StalledPump_ProducesActComponent_PlusHealthyAggregate()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
            new("fan1", "Front Fan", "fan", 1200, 50, CoolingStallStatuses.Ok, null),
        });

        var result = Compute(cooling: cooling);

        var stalled = Assert.Single(result.Components, c => c.Id == "cooling:pump1");
        Assert.Equal(HealthStatuses.Act, stalled.Status);
        Assert.Equal("cooling.pumpStall", Assert.Single(stalled.Reasons).Code);

        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Ok, aggregate.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void Cooling_EvaluatedEvenWhenNotWindowsSupported()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });

        var result = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: cooling,
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        // A working sub-domain (cooling) produced a component, so the grid is
        // supported off-Windows too.
        Assert.True(result.Supported);
        Assert.Single(result.Components, c => c.Kind == "cooling" && c.Status == HealthStatuses.Act);
        Assert.DoesNotContain(result.Components, c => c.Kind is "storage" or "gpu" or "memory" or "system");
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void StorageAndGpu_ContributeOffWindows_WhenTheirSnapshotsAreSupported()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new() { Id = "storage:NVME0", Name = "Linux NVMe", Status = "good", DetailedReasons = new List<SmartReason>() },
            },
        };
        var gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
        {
            new("Linux GPU", "1.0", 50, 100, new GpuThrottleInfo(Array.Empty<string>(), null, null, null, null)),
        });

        var result = DiagnosticsHealthModel.Compute(
            smart: smart,
            cooling: EmptyCooling,
            gpu: gpu,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: EmptyPnp,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        // SMART (smartctl) and GPU (NVML) health surface off Windows; memory
        // and system/pnp stay Windows-only, so no tile appears for them.
        Assert.True(result.Supported);
        Assert.Single(result.Components, c => c.Kind == "storage");
        Assert.Single(result.Components, c => c.Kind == "gpu");
        Assert.DoesNotContain(result.Components, c => c.Kind is "memory" or "system");
    }

    [Fact]
    public void GpuPlaceholder_FromKnownModels_StaysWindowsOnly()
    {
        // Off Windows with no live GPU health (NVML unsupported) but a model
        // name present (system_profiler/lspci), the Unknown placeholder tile
        // must NOT appear - it would flip Supported true with no real signal.
        var offWindows = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: new CoolingStallSnapshot(true, new List<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: new[] { "Apple M1 Max" },
            windowsSupported: false,
            generatedAtUtc: T0);
        Assert.DoesNotContain(offWindows.Components, c => c.Kind == "gpu");
        Assert.False(offWindows.Supported);

        // On Windows the same inputs still produce the Unknown placeholder.
        var onWindows = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: new CoolingStallSnapshot(true, new List<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: new[] { "NVIDIA GeForce RTX 4080" },
            windowsSupported: true,
            generatedAtUtc: T0);
        Assert.Single(onWindows.Components, c => c.Kind == "gpu");
    }

    [Fact]
    public void NotSupported_OffWindows_WhenNoSubDomainProducedAComponent()
    {
        var result = DiagnosticsHealthModel.Compute(
            smart: SmartSnapshotUnsupported(),
            cooling: new CoolingStallSnapshot(true, new List<CoolingStallDevice>()),
            gpu: GpuHealthSnapshot.Unsupported,
            counts30d: new Dictionary<string, int>(),
            lastMemoryTest: null,
            pnp: PnpProblemSnapshot.Unsupported,
            knownGpuModels: Array.Empty<string>(),
            windowsSupported: false,
            generatedAtUtc: T0);

        Assert.False(result.Supported);
        Assert.Empty(result.Components);
    }

    [Fact]
    public void MemoryTestFailed_IsAct()
    {
        var lastTest = new MemoryTestResult(T0, MemoryTestResult.Failed, "1202 error");

        var result = Compute(lastMemoryTest: lastTest);

        var memory = Assert.Single(result.Components, c => c.Kind == "memory");
        Assert.Equal(HealthStatuses.Act, memory.Status);
    }

    private static SmartSnapshot SmartSnapshotUnsupported() => new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };

    [Fact]
    public void SustainedHighTemp_OngoingEpisode_AddsWatchReasonToCoolingAggregate()
    {
        // Ends exactly at "now" - still hot at generation time.
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0, 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode });

        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
        var reason = Assert.Single(aggregate.Reasons);
        Assert.Equal("cooling.sustainedHighTemp", reason.Code);
        Assert.Equal(HealthStatuses.Watch, reason.Severity);
        Assert.Contains("RTX 5080", reason.Summary);
        Assert.Equal(HealthStatuses.Watch, result.Overall);
    }

    [Fact]
    public void SustainedHighTemp_EpisodeEndedBeforeTheRecencyWindow_ProducesNoReason()
    {
        // Default linger is 0, so the recency window is one bucket (5 min);
        // an episode that cooled 20 minutes ago is well outside it.
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0.AddMinutes(-20), 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode });

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void SustainedHighTemp_NoEpisodes_AggregateOmittedWhenNoCoolingDevicesEither()
    {
        var result = Compute();

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
    }

    [Fact]
    public void SustainedHighTemp_CombinesWithStalledPump_WorstStatusWins()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });
        var episode = new TemperatureEpisode("cpu", "CPU", T0.AddHours(-1), T0, 92, 90, "cpu");

        var result = Compute(cooling: cooling, tempEpisodes: new[] { episode });

        var stalled = Assert.Single(result.Components, c => c.Id == "cooling:pump1");
        Assert.Equal(HealthStatuses.Act, stalled.Status);
        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
        Assert.Equal(HealthStatuses.Act, result.Overall);
    }

    [Fact]
    public void WarningLingerMinutes_KeepsAPastEpisodeVisibleWithinTheLingerWindow()
    {
        var diagnostics = new DiagnosticsSettings { WarningLingerMinutes = 30 };
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0.AddMinutes(-20), 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
    }

    [Fact]
    public void WarningLingerMinutes_DoesNotKeepAnEpisodeVisibleBeyondTheLingerWindow()
    {
        var diagnostics = new DiagnosticsSettings { WarningLingerMinutes = 30 };
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0.AddMinutes(-40), 91.5, 85, "gpu");

        var result = Compute(tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
    }

    [Fact]
    public void DisabledStorageComponent_ExcludedFromStatusAndOverall()
    {
        var smart = new SmartSnapshot
        {
            Supported = true,
            Drives = new List<SmartDriveInfo>
            {
                new()
                {
                    Id = "storage:ABC123",
                    Name = "Test SSD",
                    Status = "bad",
                    DetailedReasons = new List<SmartReason>
                    {
                        new("nvme.criticalWarning", ReasonSeverity.Act, "critical", "detail"),
                    },
                },
            },
        };
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Storage = false;

        var result = Compute(smart: smart, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Kind == "storage");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void DisabledCpuComponent_ExcludesCpuTempEpisodeFromCoolingAggregate()
    {
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Cpu = false;
        var episode = new TemperatureEpisode("cpu", "CPU", T0.AddHours(-1), T0, 95, 90, "cpu");

        var result = Compute(tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }

    [Fact]
    public void DisabledCoolingComponent_ExcludesStallDevices_ButNotOtherKindsTempWarnings()
    {
        var cooling = new CoolingStallSnapshot(true, new List<CoolingStallDevice>
        {
            new("pump1", "Q60 Pump", "pump", 0, 60, CoolingStallStatuses.Stalled, T0),
        });
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Cooling = false;
        var episode = new TemperatureEpisode("gpu:0", "RTX 5080", T0.AddHours(-1), T0, 95, 85, "gpu");

        var result = Compute(cooling: cooling, tempEpisodes: new[] { episode }, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Id == "cooling:pump1");
        var aggregate = Assert.Single(result.Components, c => c.Id == "cooling");
        Assert.Equal(HealthStatuses.Watch, aggregate.Status);
    }

    [Fact]
    public void DisabledGpuComponent_ExcludesGpuThrottleComponent()
    {
        var gpu = new GpuHealthSnapshot(true, new List<GpuInfo>
        {
            new("Test GPU", "1.0", 50, 100, new GpuThrottleInfo(new[] { "hwThermal" }, null, null, null, null)),
        });
        var diagnostics = new DiagnosticsSettings();
        diagnostics.Components.Gpu = false;

        var result = Compute(gpu: gpu, diagnostics: diagnostics);

        Assert.DoesNotContain(result.Components, c => c.Kind == "gpu");
        Assert.Equal(HealthStatuses.Ok, result.Overall);
    }
}
