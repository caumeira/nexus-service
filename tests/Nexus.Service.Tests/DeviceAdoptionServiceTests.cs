using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices;
using Nexus.Service.Models.Conflicts;
using Xunit;

namespace Nexus.Service.Tests;

public class DeviceAdoptionServiceTests
{
    private sealed class FakeConflictDetector : IConflictDetector
    {
        public bool DetectionReady { get; set; } = true;

        public HashSet<string> RunningApps { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsAppRunning(string appId) => RunningApps.Contains(appId);

        public IReadOnlyList<DetectedConflict> GetConflicts() =>
            RunningApps.Select(id => new DetectedConflict { Id = id }).ToList();
    }

    [Fact]
    public void ShouldAdopt_ConnectedUnsetConflictDevice_AppAbsent_ReturnsTrue()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var detector = new FakeConflictDetector();

        Assert.True(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: true, gate, detector));
    }

    [Fact]
    public void ShouldAdopt_AppRunning_ReturnsFalse()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var detector = new FakeConflictDetector();
        detector.RunningApps.Add("lian-li-l-connect");

        Assert.False(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: true, gate, detector));
    }

    [Fact]
    public void ShouldAdopt_Disconnected_ReturnsFalse()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var detector = new FakeConflictDetector();

        Assert.False(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: false, gate, detector));
    }

    [Fact]
    public void ShouldAdopt_NonConflictHandler_ReturnsFalse()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var detector = new FakeConflictDetector();

        Assert.False(DeviceAdoptionService.ShouldAdopt("cnvs", connected: true, gate, detector));
    }

    [Fact]
    public void ShouldAdopt_ExplicitlyDisabledDevice_ReturnsFalse()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("lianli-wireless", false);
        var detector = new FakeConflictDetector();

        Assert.False(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: true, gate, detector));
    }

    [Fact]
    public void ShouldAdopt_AlreadyEnabledDevice_ReturnsFalse()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("lianli-wireless", true);
        var detector = new FakeConflictDetector();

        Assert.False(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: true, gate, detector));
    }

    [Fact]
    public void ShouldAdopt_DetectionNotReady_ReturnsFalse()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        var detector = new FakeConflictDetector { DetectionReady = false };

        Assert.False(DeviceAdoptionService.ShouldAdopt("lianli-wireless", connected: true, gate, detector));
    }
}
