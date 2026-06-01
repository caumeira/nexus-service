using System.IO;
using System.Linq;
using Nexus.Service.Devices.Detection;
using Xunit;

namespace Nexus.Service.Tests;

public class UsbEnumeratorTests
{
    // ---- Windows pnputil parser (present devices, with /properties) ----

    [Fact]
    public void Windows_Prefers_BusReportedDeviceDesc_Over_DeviceDescription()
    {
        // pnputil /enum-devices /connected /properties format. The USB iProduct
        // string (BusReportedDeviceDesc) is the brand-friendly name; Device
        // Description for HID interface nodes is the generic "USB Input Device".
        var output = string.Join('\n', new[]
        {
            "Instance ID:                USB\\VID_1B1C&PID_1B2E&MI_00\\9&1357d11b&1&0000",
            "Device Description:         USB Input Device",
            "Class Name:                 HIDClass",
            "Manufacturer Name:          (Standard system devices)",
            "Status:                     Started",
            "Driver Name:                input.inf",
            "Properties:",
            "    DEVPKEY_Device_DeviceDesc [String]:",
            "        USB Input Device",
            "    DEVPKEY_Device_BusReportedDeviceDesc [String]:",
            "        Corsair Gaming M65 Pro RGB Mouse",
            "    DEVPKEY_Device_LocationInfo [String]:",
            "        000e.0000.0000.016.000.000.000.000.000",
            "",
        });

        var entry = Assert.Single(PnpUtilParser.Parse(output));
        Assert.Equal(0x1B1C, entry.VendorId);
        Assert.Equal(0x1B2E, entry.ProductId);
        Assert.Equal("Corsair Gaming M65 Pro RGB Mouse", entry.Name);
        Assert.Equal("(Standard system devices)", entry.Manufacturer);
        Assert.Equal("HIDClass", entry.Class);
        Assert.Equal("input.inf", entry.Driver);
        Assert.Equal("000e.0000.0000.016.000.000.000.000.000", entry.Location);
    }

    [Fact]
    public void Windows_Falls_Back_To_DeviceDescription_When_BusReported_Missing()
    {
        var output = string.Join('\n', new[]
        {
            "Instance ID:                USB\\VID_046D&PID_C548\\9&abcdef&0&0",
            "Device Description:         Logitech USB Receiver",
            "Manufacturer Name:          Logitech",
            "Properties:",
            "    DEVPKEY_Device_LocationInfo [String]:",
            "        Port_#0003.Hub_#0001",
            "",
        });

        var entry = Assert.Single(PnpUtilParser.Parse(output));
        Assert.Equal("Logitech USB Receiver", entry.Name);
        Assert.Equal("Port_#0003.Hub_#0001", entry.Location);
    }

    [Fact]
    public void Windows_Dedupes_Composite_Interfaces_By_BusReportedDeviceDesc()
    {
        // All three nodes of a composite device share BusReportedDeviceDesc;
        // dedupe by (VID, PID, Name) collapses them to one row.
        var output = string.Join('\n', new[]
        {
            "Instance ID:                USB\\VID_1B1C&PID_1B2E\\SERIAL",
            "Device Description:         USB Composite Device",
            "Manufacturer Name:          (Standard USB Host Controller)",
            "Properties:",
            "    DEVPKEY_Device_BusReportedDeviceDesc [String]:",
            "        Corsair Gaming M65 Pro RGB Mouse",
            "",
            "Instance ID:                USB\\VID_1B1C&PID_1B2E&MI_00\\9&1&0",
            "Device Description:         USB Input Device",
            "Manufacturer Name:          (Standard system devices)",
            "Properties:",
            "    DEVPKEY_Device_BusReportedDeviceDesc [String]:",
            "        Corsair Gaming M65 Pro RGB Mouse",
            "",
            "Instance ID:                USB\\VID_1B1C&PID_1B2E&MI_01\\9&1&1",
            "Device Description:         USB Input Device",
            "Manufacturer Name:          (Standard system devices)",
            "Properties:",
            "    DEVPKEY_Device_BusReportedDeviceDesc [String]:",
            "        Corsair Gaming M65 Pro RGB Mouse",
            "",
        });

        var entry = Assert.Single(PnpUtilParser.Parse(output));
        Assert.Equal("Corsair Gaming M65 Pro RGB Mouse", entry.Name);
    }

    [Fact]
    public void Windows_Skips_NonUsb_And_RootHub_Entries()
    {
        var output = string.Join('\n', new[]
        {
            "Instance ID:                USB\\ROOT_HUB30\\5&18297c0c&0&0",
            "Device Description:         USB Root Hub (USB 3.0)",
            "",
            "Instance ID:                PCI\\VEN_1022&DEV_14E3",
            "Device Description:         PCI Device",
            "",
            "Instance ID:                USB\\VID_1B1C&PID_1B2E\\9&xyz&1&0",
            "Device Description:         Real Device",
            "Manufacturer Name:          Corsair",
            "",
        });

        var entry = Assert.Single(PnpUtilParser.Parse(output));
        Assert.Equal("Real Device", entry.Name);
        Assert.Equal("Corsair", entry.Manufacturer);
    }

    [Fact]
    public void Windows_Empty_Output_Returns_Empty_List()
    {
        Assert.Empty(PnpUtilParser.Parse(""));
        Assert.Empty(PnpUtilParser.Parse("   \n  \n"));
    }

    // ---- Mac JSON parser ----

    [Fact]
    public void Mac_Extracts_Manufacturer_From_Vendor_Parens()
    {
        var json = """
        {
          "SPUSBDataType": [
            {
              "_items": [
                {
                  "_name": "CNVS Controller",
                  "vendor_id": "0x3402 (HYTE)",
                  "product_id": "0x0BFF",
                  "serial_num": "A1B2C3",
                  "location_id": "0x14100000 / 1",
                  "speed": "full_speed"
                }
              ]
            }
          ]
        }
        """;

        var entries = MacUsbEnumerator.ParseJson(json);

        var e = Assert.Single(entries);
        Assert.Equal(0x3402, e.VendorId);
        Assert.Equal(0x0BFF, e.ProductId);
        Assert.Equal("CNVS Controller", e.Name);
        Assert.Equal("HYTE", e.Manufacturer);
        Assert.Equal("A1B2C3", e.Serial);
        Assert.Equal("0x14100000 / 1", e.Location);
        Assert.Equal("full_speed", e.Speed);
        Assert.Equal("USB\\VID_3402&PID_0BFF", e.HardwareId);
    }

    [Fact]
    public void Mac_Recurses_Into_Nested_Items()
    {
        // USB trees are hubs-containing-devices. Nested _items must be visited.
        var json = """
        {
          "SPUSBDataType": [
            {
              "_items": [
                {
                  "_name": "Hub",
                  "_items": [
                    {
                      "_name": "Keyboard",
                      "vendor_id": "0x05AC",
                      "product_id": "0x024F"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var entries = MacUsbEnumerator.ParseJson(json);

        var e = Assert.Single(entries);
        Assert.Equal(0x05AC, e.VendorId);
        Assert.Equal(0x024F, e.ProductId);
        Assert.Equal("Keyboard", e.Name);
    }

    // ---- Linux sysfs reader ----

    [NonWindowsFact]
    public void Linux_Reads_Full_Device_Tree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexus-usb-test-{Path.GetRandomFileName()}");
        try
        {
            var deviceDir = Path.Combine(tempRoot, "1-2");
            Directory.CreateDirectory(deviceDir);
            File.WriteAllText(Path.Combine(deviceDir, "idVendor"), "3402\n");
            File.WriteAllText(Path.Combine(deviceDir, "idProduct"), "0bff\n");
            File.WriteAllText(Path.Combine(deviceDir, "product"), "CNVS Controller\n");
            File.WriteAllText(Path.Combine(deviceDir, "manufacturer"), "HYTE\n");
            File.WriteAllText(Path.Combine(deviceDir, "serial"), "A1B2C3\n");
            File.WriteAllText(Path.Combine(deviceDir, "busnum"), "1\n");
            File.WriteAllText(Path.Combine(deviceDir, "devnum"), "4\n");
            File.WriteAllText(Path.Combine(deviceDir, "speed"), "480\n");
            File.WriteAllText(Path.Combine(deviceDir, "bDeviceClass"), "03\n");

            // Root hub entry that should be skipped.
            var rootHub = Path.Combine(tempRoot, "usb1");
            Directory.CreateDirectory(rootHub);
            File.WriteAllText(Path.Combine(rootHub, "idVendor"), "1d6b");
            File.WriteAllText(Path.Combine(rootHub, "idProduct"), "0002");

            // Interface entry that should be skipped.
            var ifaceDir = Path.Combine(tempRoot, "1-2:1.0");
            Directory.CreateDirectory(ifaceDir);

            var entries = LinuxUsbEnumerator.EnumerateFrom(tempRoot);

            var e = Assert.Single(entries);
            Assert.Equal(0x3402, e.VendorId);
            Assert.Equal(0x0BFF, e.ProductId);
            Assert.Equal("CNVS Controller", e.Name);
            Assert.Equal("HYTE", e.Manufacturer);
            Assert.Equal("A1B2C3", e.Serial);
            Assert.Equal("Bus 001 Device 004", e.Location);
            Assert.Equal("High", e.Speed);
            Assert.Equal("HID", e.Class);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Linux_Missing_Sysfs_Root_Returns_Empty()
    {
        var entries = LinuxUsbEnumerator.EnumerateFrom("/tmp/definitely-not-a-sysfs-dir-xyz123");
        Assert.Empty(entries);
    }

    [Fact]
    public void Linux_Maps_All_Speed_Buckets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexus-usb-speed-{Path.GetRandomFileName()}");
        try
        {
            string[][] cases =
            {
                new[] { "1.5", "Low" },
                new[] { "12", "Full" },
                new[] { "480", "High" },
                new[] { "5000", "Super" },
                new[] { "10000", "SuperPlus" },
            };

            for (var i = 0; i < cases.Length; i++)
            {
                var dir = Path.Combine(tempRoot, $"1-{i}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "idVendor"), "1234");
                File.WriteAllText(Path.Combine(dir, "idProduct"), $"{i + 1:X4}");
                File.WriteAllText(Path.Combine(dir, "speed"), cases[i][0]);
            }

            var entries = LinuxUsbEnumerator.EnumerateFrom(tempRoot);
            var bySpeed = entries.Select(e => e.Speed).ToHashSet();
            foreach (var c in cases)
            {
                Assert.Contains(c[1], bySpeed);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
