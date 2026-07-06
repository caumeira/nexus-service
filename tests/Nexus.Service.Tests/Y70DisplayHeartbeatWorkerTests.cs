using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Detection-edge re-orient behavior: a monitor-only Y70 (no serial, no USB
/// enumeration) must still trip the effective-orientation apply exactly once
/// per newly-detected edge, since the heartbeat's serial poll below is gated
/// on USB presence and never sees it.
/// </summary>
public class Y70DisplayHeartbeatWorkerTests
{
    private static Y70DisplayHeartbeatWorker Build(FakeY70Provider y70)
    {
        var hub = new Y70DisplayHub(
            new StubY70DisplayPortDiscovery(),
            _ => throw new InvalidOperationException("Y70 transport is not expected in these tests"));
        var presence = new HardwarePresence(new EmptyUsbEnumerator());
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        return new Y70DisplayHeartbeatWorker(hub, presence, gate, y70);
    }

    [Fact]
    public void Monitor_only_detection_applies_orientation_once_per_edge()
    {
        var y70 = new FakeY70Provider { Connected = true };
        var worker = Build(y70);

        worker.Tick();
        worker.Tick();
        worker.Tick();

        Assert.Equal(1, y70.ApplyCount);
    }

    [Fact]
    public void Sustained_disconnect_then_reconnect_reapplies_orientation()
    {
        var y70 = new FakeY70Provider { Connected = true };
        var worker = Build(y70);

        worker.Tick();               // detected -> apply (1)
        y70.Connected = false;
        for (var i = 0; i < 5; i++) worker.Tick();   // sustained absence past the debounce
        y70.Connected = true;
        worker.Tick();               // genuine re-detect -> apply (2)

        Assert.Equal(2, y70.ApplyCount);
    }

    [Fact]
    public void Transient_absence_does_not_reapply_orientation()
    {
        // A single-tick absence models the Y70 dropping out of enumeration
        // mid-rotation. It must NOT re-arm the edge, or the apply loops.
        var y70 = new FakeY70Provider { Connected = true };
        var worker = Build(y70);

        worker.Tick();               // detected -> apply (1)
        y70.Connected = false;
        worker.Tick();               // one transient miss (within debounce)
        y70.Connected = true;
        worker.Tick();               // back -> must NOT re-apply

        Assert.Equal(1, y70.ApplyCount);
    }

    [Fact]
    public void Never_detected_never_applies_orientation()
    {
        var y70 = new FakeY70Provider { Connected = false };
        var worker = Build(y70);

        worker.Tick();
        worker.Tick();

        Assert.Equal(0, y70.ApplyCount);
    }

    private sealed class EmptyUsbEnumerator : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class FakeY70Provider : IY70Provider
    {
        public bool Connected;
        public int ApplyCount;

        public bool IsConnected() => Connected;
        public string GetOrientation() => "PortraitFlipped";
        public void SetOrientation(string orientation) { }
        public bool GetForceOrientation() => true;
        public void SetForceOrientation(bool forceOrientation) { }
        public void ApplyEffectiveOrientation() => ApplyCount++;
        public int GetBrightness() => 0;
        public void SetBrightness(int brightness) { }
        public bool GetToggle() => false;
        public void SetToggle(bool toggle) { }
        public bool IsRotated() => true;
    }

    /// <summary>In-memory IConfigStore for unit tests - no disk I/O.</summary>
    private sealed class InMemoryConfigStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();

        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
