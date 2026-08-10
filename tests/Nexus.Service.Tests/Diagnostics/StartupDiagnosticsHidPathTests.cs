using Nexus.Service.Diagnostics;

namespace Nexus.Service.Tests.Diagnostics;

/// <summary>Interface and collection rendering for the startup snapshot's hid block.</summary>
public class StartupDiagnosticsHidPathTests
{
    [Fact]
    public void InterfaceAndCollection_AreBothRendered()
    {
        const string path = @"\\?\HID#VID_1B1C&PID_1B7C&MI_01&Col02#8&2d59ea83&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(" mi=01 col=02", StartupDiagnosticsDumpService.DescribeHidPath(path));
    }

    [Fact]
    public void InterfaceOnly_OmitsCollection()
    {
        const string path = @"\\?\HID#VID_046D&PID_C52B&MI_02#7&1e3a1f0d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(" mi=02", StartupDiagnosticsDumpService.DescribeHidPath(path));
    }

    [Fact]
    public void CollectionOnly_OmitsInterface()
    {
        const string path = @"\\?\HID#VID_0CF2&PID_A102&Col01#c&7b0cf2a&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal(" col=01", StartupDiagnosticsDumpService.DescribeHidPath(path));
    }

    [Fact]
    public void SingleInterfaceDevice_RendersNothing()
    {
        const string path = @"\\?\HID#VID_258A&PID_010C#6&33a1c2b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal("", StartupDiagnosticsDumpService.DescribeHidPath(path));
    }

    [Fact]
    public void HidrawPath_RendersNothing()
    {
        Assert.Equal("", StartupDiagnosticsDumpService.DescribeHidPath("/dev/hidraw3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    public void DegenerateInput_IsTolerated(string path)
    {
        Assert.Equal("", StartupDiagnosticsDumpService.DescribeHidPath(path));
    }

    [Fact]
    public void InstanceIdText_DoesNotFalseMatch()
    {
        // "col" and "mi_" here sit in the instance-id segment, past the hardware id.
        const string path = @"\\?\HID#VID_1B1C&PID_1B7C#8&colossus&mi_ff&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        Assert.Equal("", StartupDiagnosticsDumpService.DescribeHidPath(path));
    }
}
