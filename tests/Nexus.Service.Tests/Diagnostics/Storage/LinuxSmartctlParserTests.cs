using System.Linq;
using Nexus.Service.Diagnostics.Storage;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Storage;

/// <summary>
/// Fixtures below are synthetic - hand-authored from the smartctl 7.x JSON
/// schema (smartctl.cpp/ataprint.cpp/nvmeprint.cpp field names), not captured
/// from a real drive. Field shapes are verified against smartctl source; the
/// actual VALUES (temperatures, hour counts) are made up for these tests.
/// </summary>
public class LinuxSmartctlParserTests
{
    private const string ScanJson = """
    {
      "devices": [
        { "name": "/dev/sda", "info_name": "/dev/sda [SAT]", "type": "sat", "protocol": "ATA" },
        { "name": "/dev/nvme0", "info_name": "/dev/nvme0", "type": "nvme", "protocol": "NVMe" }
      ]
    }
    """;

    private const string NvmeDeviceJson = """
    {
      "device": { "name": "/dev/nvme0", "info_name": "/dev/nvme0", "type": "nvme", "protocol": "NVMe" },
      "model_name": "Synthetic NVMe 1TB",
      "serial_number": "NVME-SN-001",
      "smart_status": { "passed": true },
      "temperature": { "current": 38 },
      "power_on_time": { "hours": 1200 },
      "power_cycle_count": 45,
      "user_capacity": { "blocks": 1953525168, "bytes": 1000204886016 },
      "nvme_smart_health_information_log": {
        "critical_warning": 0,
        "temperature": 38,
        "available_spare": 100,
        "available_spare_threshold": 10,
        "percentage_used": 5,
        "data_units_read": 1000,
        "data_units_written": 2000,
        "power_cycles": 45,
        "power_on_hours": 1200,
        "unsafe_shutdowns": 2,
        "media_errors": 0,
        "num_err_log_entries": 0
      }
    }
    """;

    private const string SataDeviceJsonClean = """
    {
      "device": { "name": "/dev/sda", "info_name": "/dev/sda [SAT]", "type": "sat", "protocol": "ATA" },
      "model_name": "Synthetic SATA SSD 500GB",
      "serial_number": "SATA-SN-001",
      "smart_status": { "passed": true },
      "temperature": { "current": 35 },
      "power_on_time": { "hours": 5000 },
      "power_cycle_count": 100,
      "user_capacity": { "blocks": 976773168, "bytes": 500107862016 },
      "ata_smart_attributes": {
        "revision": 1,
        "table": [
          { "id": 5, "name": "Reallocated_Sector_Ct", "value": 100, "worst": 100, "thresh": 10, "raw": { "value": 0, "string": "0" } },
          { "id": 9, "name": "Power_On_Hours", "value": 90, "worst": 90, "thresh": 0, "raw": { "value": 5000, "string": "5000" } },
          { "id": 194, "name": "Temperature_Celsius", "value": 65, "worst": 50, "thresh": 0, "raw": { "value": 35, "string": "35" } }
        ]
      }
    }
    """;

    private const string SataDeviceJsonReallocated = """
    {
      "device": { "name": "/dev/sdb", "info_name": "/dev/sdb [SAT]", "type": "sat", "protocol": "ATA" },
      "model_name": "Synthetic SATA HDD 2TB",
      "serial_number": "SATA-SN-002",
      "smart_status": { "passed": true },
      "temperature": { "current": 40 },
      "power_on_time": { "hours": 20000 },
      "power_cycle_count": 300,
      "user_capacity": { "blocks": 3907029168, "bytes": 2000398934016 },
      "ata_smart_attributes": {
        "table": [
          { "id": 5, "name": "Reallocated_Sector_Ct", "value": 80, "worst": 80, "thresh": 10, "raw": { "value": 150, "string": "150" } }
        ]
      }
    }
    """;

    private const string SataDeviceJsonFailedAssessment = """
    {
      "device": { "name": "/dev/sdc", "info_name": "/dev/sdc [SAT]", "type": "sat", "protocol": "ATA" },
      "model_name": "Synthetic Dying SATA",
      "serial_number": "SATA-SN-003",
      "smart_status": { "passed": false },
      "temperature": { "current": 55 },
      "user_capacity": { "bytes": 500107862016 },
      "ata_smart_attributes": { "table": [] }
    }
    """;

    private static LinuxSmartctlParser.ScanEntry NvmeEntry => new("/dev/nvme0", "nvme");
    private static LinuxSmartctlParser.ScanEntry SataEntry => new("/dev/sda", "sat");

    [Fact]
    public void ParseScan_reads_name_and_type_for_each_device()
    {
        var devices = LinuxSmartctlParser.ParseScan(ScanJson);

        Assert.Equal(2, devices.Count);
        Assert.Equal("/dev/sda", devices[0].Name);
        Assert.Equal("sat", devices[0].Type);
        Assert.Equal("/dev/nvme0", devices[1].Name);
        Assert.Equal("nvme", devices[1].Type);
    }

    [Fact]
    public void ParseScan_empty_or_garbage_input_returns_empty_list()
    {
        Assert.Empty(LinuxSmartctlParser.ParseScan(""));
        Assert.Empty(LinuxSmartctlParser.ParseScan("not json"));
        Assert.Empty(LinuxSmartctlParser.ParseScan("{}"));
    }

    [Fact]
    public void ParseDevice_nvme_maps_model_serial_bus_and_nvme_wire_fields()
    {
        var drive = LinuxSmartctlParser.ParseDevice(NvmeDeviceJson, NvmeEntry);

        Assert.NotNull(drive);
        Assert.Equal("storage:NVME-SN-001", drive!.Id);
        Assert.Equal("Synthetic NVMe 1TB", drive.Name);
        Assert.Equal("NVME-SN-001", drive.Serial);
        Assert.Equal("nvme", drive.Bus);
        Assert.Equal(1000204886016UL, drive.SizeBytes);
        Assert.Equal(38, drive.TemperatureC);
        Assert.Equal(1200UL, drive.PowerOnHours);
        Assert.Equal(45UL, drive.PowerCycles);
        Assert.Equal("good", drive.Status);
        Assert.Empty(drive.Attributes);

        Assert.NotNull(drive.Nvme);
        Assert.Equal(0, drive.Nvme!.CriticalWarning);
        Assert.Equal(100, drive.Nvme.AvailableSpare);
        Assert.Equal(10, drive.Nvme.SpareThreshold);
        Assert.Equal(5, drive.Nvme.PercentageUsed);
        Assert.Equal(2UL, drive.Nvme.UnsafeShutdowns);
        // 1000 units * 512000 bytes/unit
        Assert.Equal(512_000_000UL, drive.Nvme.DataUnitsReadBytes);
        Assert.Equal(1_024_000_000UL, drive.Nvme.DataUnitsWrittenBytes);
    }

    [Fact]
    public void ParseDevice_sata_clean_drive_maps_attributes_and_is_good()
    {
        var drive = LinuxSmartctlParser.ParseDevice(SataDeviceJsonClean, SataEntry);

        Assert.NotNull(drive);
        Assert.Equal("storage:SATA-SN-001", drive!.Id);
        Assert.Equal("sata", drive.Bus);
        Assert.Equal(35, drive.TemperatureC);
        Assert.Equal(5000UL, drive.PowerOnHours);
        Assert.Equal(100UL, drive.PowerCycles);
        Assert.Equal("good", drive.Status);
        Assert.Null(drive.Nvme);

        Assert.Equal(3, drive.Attributes.Count);
        var reallocated = drive.Attributes.Single(a => a.Id == 5);
        Assert.Equal("Reallocated_Sector_Ct", reallocated.Name);
        Assert.Equal(0UL, reallocated.Raw);
        Assert.False(reallocated.Flagged);
    }

    [Fact]
    public void ParseDevice_sata_reallocated_sectors_over_100_is_warning_and_flagged()
    {
        var drive = LinuxSmartctlParser.ParseDevice(SataDeviceJsonReallocated, new LinuxSmartctlParser.ScanEntry("/dev/sdb", "sat"));

        Assert.NotNull(drive);
        Assert.Equal("warning", drive!.Status);
        Assert.Contains("smart.reallocated", drive.StatusReasons);
        var reallocated = Assert.Single(drive.Attributes);
        Assert.True(reallocated.Flagged);
    }

    [Fact]
    public void ParseDevice_failed_smart_assessment_maps_to_predicts_failure_bad_status()
    {
        var drive = LinuxSmartctlParser.ParseDevice(SataDeviceJsonFailedAssessment, new LinuxSmartctlParser.ScanEntry("/dev/sdc", "sat"));

        Assert.NotNull(drive);
        Assert.Equal("bad", drive!.Status);
        Assert.Contains("smart.vendorHealth", drive.StatusReasons);
    }

    [Fact]
    public void ParseDevice_empty_or_garbage_input_returns_null()
    {
        Assert.Null(LinuxSmartctlParser.ParseDevice("", SataEntry));
        Assert.Null(LinuxSmartctlParser.ParseDevice("not json", SataEntry));
    }

    [Fact]
    public void ParseDevice_open_failure_payload_with_no_identity_or_health_data_is_omitted()
    {
        // A device smartctl could not open still returns valid JSON - just a
        // smartctl.messages array, no model_name or smart_status. This must
        // not mint an empty-model drive with Status "good".
        const string json = """
        {
          "device": { "name": "/dev/sdy", "type": "sat", "protocol": "ATA" },
          "smartctl": {
            "exit_status": 2,
            "messages": [ { "string": "Permission denied", "severity": "error" } ]
          }
        }
        """;

        var drive = LinuxSmartctlParser.ParseDevice(json, new LinuxSmartctlParser.ScanEntry("/dev/sdy", "sat"));

        Assert.Null(drive);
    }

    [Fact]
    public void ParseDevice_usb_bridge_type_classifies_as_usb_bus()
    {
        const string json = """
        {
          "device": { "name": "/dev/sdz", "type": "usbjmicron", "protocol": "ATA" },
          "model_name": "Synthetic USB Enclosure",
          "serial_number": "USB-SN-001",
          "smart_status": { "passed": true },
          "user_capacity": { "bytes": 4000787030016 }
        }
        """;

        var drive = LinuxSmartctlParser.ParseDevice(json, new LinuxSmartctlParser.ScanEntry("/dev/sdz", "usbjmicron"));

        Assert.NotNull(drive);
        Assert.Equal("usb", drive!.Bus);
    }

    [Theory]
    [InlineData(null, (ushort)0)]
    [InlineData(0.0, (ushort)273)]
    [InlineData(-500.0, (ushort)0)]
    [InlineData(1e10, ushort.MaxValue)]
    public void ToKelvinUShort_clamps_out_of_range_values_instead_of_wrapping(double? celsius, ushort expected)
    {
        Assert.Equal(expected, LinuxSmartctlParser.ToKelvinUShort(celsius));
    }
}
