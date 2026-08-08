using System.Linq;
using Nexus.Service.Diagnostics.Storage;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Storage;

/// <summary>Covers the smartctl-missing/zero-device/multi-device caching
/// behavior via injected command seams - no real smartctl needed.</summary>
public class LinuxSmartHealthMonitorTests
{
    private const string ScanOneDevice = """
    { "devices": [ { "name": "/dev/sda", "type": "sat", "protocol": "ATA" } ] }
    """;

    private const string SataDeviceJson = """
    {
      "device": { "name": "/dev/sda", "type": "sat", "protocol": "ATA" },
      "model_name": "Synthetic Drive",
      "serial_number": "SN-1",
      "smart_status": { "passed": true },
      "user_capacity": { "bytes": 500107862016 }
    }
    """;

    [Fact]
    public void Snapshot_when_scan_returns_empty_string_is_unsupported()
    {
        var monitor = new LinuxSmartHealthMonitor(() => "", _ => "");

        var snapshot = monitor.Snapshot();

        Assert.False(snapshot.Supported);
        Assert.Empty(snapshot.Drives);
    }

    [Fact]
    public void Snapshot_when_scan_finds_zero_devices_is_supported_with_empty_list()
    {
        var monitor = new LinuxSmartHealthMonitor(() => """{ "devices": [] }""", _ => "");

        var snapshot = monitor.Snapshot();

        Assert.True(snapshot.Supported);
        Assert.Empty(snapshot.Drives);
    }

    [Fact]
    public void Snapshot_reads_scanned_device_and_reports_supported()
    {
        var monitor = new LinuxSmartHealthMonitor(() => ScanOneDevice, _ => SataDeviceJson);

        var snapshot = monitor.Snapshot();

        Assert.True(snapshot.Supported);
        var drive = Assert.Single(snapshot.Drives);
        Assert.Equal("storage:SN-1", drive.Id);
        Assert.Equal("Synthetic Drive", drive.Name);
    }

    [Fact]
    public void Snapshot_when_a_device_query_throws_skips_that_drive_without_crashing()
    {
        var monitor = new LinuxSmartHealthMonitor(() => ScanOneDevice, _ => throw new System.InvalidOperationException("boom"));

        var snapshot = monitor.Snapshot();

        Assert.True(snapshot.Supported);
        Assert.Empty(snapshot.Drives);
    }

    [Fact]
    public void Snapshot_is_cached_between_calls_within_the_refresh_window()
    {
        var calls = 0;
        var monitor = new LinuxSmartHealthMonitor(() =>
        {
            calls++;
            return ScanOneDevice;
        }, _ => SataDeviceJson);

        monitor.Snapshot();
        monitor.Snapshot();
        monitor.Snapshot();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ForceRefresh_within_the_floor_window_of_the_last_refresh_is_a_no_op()
    {
        // Mirrors SmartHealthMonitor's floor: a ForceRefresh immediately
        // after a Snapshot must not re-query, so a stuck client retry loop
        // can't hammer smartctl continuously.
        var calls = 0;
        var monitor = new LinuxSmartHealthMonitor(() =>
        {
            calls++;
            return ScanOneDevice;
        }, _ => SataDeviceJson);

        monitor.Snapshot();
        monitor.ForceRefresh();

        Assert.Equal(1, calls);
    }
}
