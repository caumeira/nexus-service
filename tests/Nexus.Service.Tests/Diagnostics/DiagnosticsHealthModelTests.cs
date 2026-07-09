using System;
using System.Collections.Generic;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
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
        IReadOnlyList<string>? knownGpuModels = null)
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
            generatedAtUtc: T0);
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

        Assert.False(result.Supported);
        Assert.Single(result.Components, c => c.Kind == "cooling" && c.Status == HealthStatuses.Act);
        Assert.DoesNotContain(result.Components, c => c.Kind is "storage" or "gpu" or "memory" or "system");
        Assert.Equal(HealthStatuses.Act, result.Overall);
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
}
