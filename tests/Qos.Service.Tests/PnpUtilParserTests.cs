using Qos.Service.Devices.Detection;

namespace Qos.Service.Tests;

/// <summary>
/// Direct unit tests for the PnpUtilParser rewrite. The parser runs on
/// 500 KB - 2 MB blobs of `pnputil /enum-devices /connected /properties`
/// output every USB cache refresh, so regressions here regress CPU and
/// allocation behaviour on every Devices-tab poll on Windows. The fixtures
/// below are minimal but cover the structural edge cases the original
/// code has to handle in the wild: composite interfaces, properties-value
/// indentation, root-hub filtering, non-USB entries, missing properties,
/// and blank-line block terminators.
/// </summary>
public class PnpUtilParserTests
{
    // Full well-formed output: two distinct USB devices plus a root hub
    // that must be filtered and a non-USB PCI entry that must be ignored.
    private const string SampleFull =
        """
        Instance ID:                USB\VID_1532&PID_007D\9&2A7B3C1D&0&0004
        Device Description:         USB Input Device
        Class Name:                 HIDClass
        Manufacturer Name:          (Standard system devices)
        Driver Name:                input.inf
        Properties:
            DEVPKEY_Device_BusReportedDeviceDesc [String]:
                Razer DeathAdder V2 Pro
            DEVPKEY_Device_LocationInfo [String]:
                Port_#0004.Hub_#0001
            DEVPKEY_Device_FirstInstallDate [FILETIME]:
                4/15/2026 10:22:31 AM

        Instance ID:                USB\VID_1B1C&PID_1B8E\A2BC3D4E&0
        Device Description:         USB Input Device
        Class Name:                 HIDClass
        Manufacturer Name:          (Standard system devices)
        Driver Name:                input.inf
        Properties:
            DEVPKEY_Device_BusReportedDeviceDesc [String]:
                Corsair Gaming M65 Pro RGB Mouse
            DEVPKEY_Device_LocationInfo [String]:
                Port_#0002.Hub_#0001

        Instance ID:                USB\ROOT_HUB30\4&1E8A9B&0&0
        Device Description:         USB Root Hub (USB 3.0)
        Class Name:                 USB
        Driver Name:                usbhub3.inf
        Properties:
            DEVPKEY_Device_LocationInfo [String]:
                (Standard)

        Instance ID:                PCI\VEN_10DE&DEV_2684&SUBSYS_40961458\4&12345&0&0008
        Device Description:         NVIDIA GeForce RTX 4090
        Class Name:                 Display
        Manufacturer Name:          NVIDIA
        Driver Name:                nvlt.inf

        """;

    [Fact]
    public void Parse_Empty_ReturnsEmpty()
    {
        Assert.Empty(PnpUtilParser.Parse(""));
        Assert.Empty(PnpUtilParser.Parse("   \n  \n"));
        Assert.Empty(PnpUtilParser.Parse(null!));
    }

    [Fact]
    public void Parse_ExtractsBothUsbDevices()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public void Parse_ExtractsVidPid_AsIntegers()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        var razer = devices.Single(d => d.VendorId == 0x1532);
        Assert.Equal(0x007D, razer.ProductId);

        var corsair = devices.Single(d => d.VendorId == 0x1B1C);
        Assert.Equal(0x1B8E, corsair.ProductId);
    }

    [Fact]
    public void Parse_PrefersBusReportedDeviceDesc_OverDeviceDescription()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        var razer = devices.Single(d => d.VendorId == 0x1532);
        Assert.Equal("Razer DeathAdder V2 Pro", razer.Name);

        var corsair = devices.Single(d => d.VendorId == 0x1B1C);
        Assert.Equal("Corsair Gaming M65 Pro RGB Mouse", corsair.Name);
    }

    [Fact]
    public void Parse_FallsBackToDeviceDescription_WhenBusReportedMissing()
    {
        const string noBusReported =
            """
            Instance ID:                USB\VID_046D&PID_C539\12345
            Device Description:         USB Composite Device
            Class Name:                 USB

            """;
        var devices = PnpUtilParser.Parse(noBusReported);
        Assert.Single(devices);
        Assert.Equal("USB Composite Device", devices[0].Name);
    }

    [Fact]
    public void Parse_FiltersRootHubs()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        Assert.DoesNotContain(devices, d => d.HardwareId.Contains("ROOT_HUB"));
    }

    [Fact]
    public void Parse_SkipsNonUsbEntries()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        // The PCI NVIDIA entry must not appear.
        Assert.DoesNotContain(devices, d => d.Name.Contains("NVIDIA"));
    }

    [Fact]
    public void Parse_CapturesLocationInfo()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        var razer = devices.Single(d => d.VendorId == 0x1532);
        Assert.Equal("Port_#0004.Hub_#0001", razer.Location);
    }

    [Fact]
    public void Parse_CapturesDriverAndClass()
    {
        var devices = PnpUtilParser.Parse(SampleFull);
        var razer = devices.Single(d => d.VendorId == 0x1532);
        Assert.Equal("HIDClass", razer.Class);
        Assert.Equal("input.inf", razer.Driver);
    }

    [Fact]
    public void Parse_DedupesCompositeInterfaces_ByVidPidName()
    {
        // Composite device: two interface rows (MI_00 and MI_01) share the
        // same VID/PID and BusReportedDeviceDesc. Parser should collapse.
        const string composite =
            """
            Instance ID:                USB\VID_1532&PID_007D&MI_00\9&ABC&0&0000
            Device Description:         USB Input Device
            Class Name:                 HIDClass
            Driver Name:                input.inf
            Properties:
                DEVPKEY_Device_BusReportedDeviceDesc [String]:
                    Razer DeathAdder V2 Pro

            Instance ID:                USB\VID_1532&PID_007D&MI_01\9&ABC&0&0001
            Device Description:         USB Input Device
            Class Name:                 HIDClass
            Driver Name:                input.inf
            Properties:
                DEVPKEY_Device_BusReportedDeviceDesc [String]:
                    Razer DeathAdder V2 Pro

            """;
        var devices = PnpUtilParser.Parse(composite);
        Assert.Single(devices);
    }

    [Fact]
    public void Parse_IgnoresMalformedInstanceIds()
    {
        const string malformed =
            """
            Instance ID:                USB\not_a_valid_format
            Device Description:         Broken

            Instance ID:                USB\VID_ABCD&PID_1234\GOOD
            Device Description:         Fine Device
            Class Name:                 USB

            """;
        var devices = PnpUtilParser.Parse(malformed);
        Assert.Single(devices);
        Assert.Equal(0xABCD, devices[0].VendorId);
    }

    [Fact]
    public void Parse_HandlesTrailingOutputWithoutBlankLine()
    {
        // Real pnputil output always ends with a blank line, but defensive:
        // the parser must still emit the last device if the blob is truncated.
        const string noTrailingBlank =
            "Instance ID:                USB\\VID_046D&PID_C539\\12345\n" +
            "Device Description:         USB Composite Device\n" +
            "Class Name:                 USB";
        var devices = PnpUtilParser.Parse(noTrailingBlank);
        Assert.Single(devices);
        Assert.Equal(0x046D, devices[0].VendorId);
    }
}
