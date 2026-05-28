using System.IO;
using Nexus.Service.Devices.Firmware;
using Xunit;

namespace Nexus.Service.Tests;

public class BundledFirmwareCatalogTests
{
    private readonly BundledFirmwareCatalog _catalog = new();

    [Fact]
    public void Scans_the_embedded_firmware_for_every_bundled_device()
    {
        Assert.Contains("np50", _catalog.DeviceIds);
        Assert.Contains("cnvs", _catalog.DeviceIds);
        Assert.Contains("fan-hub", _catalog.DeviceIds);
        Assert.Contains("q60", _catalog.DeviceIds);
        Assert.Contains("q80", _catalog.DeviceIds);
        Assert.Contains("y70-touch", _catalog.DeviceIds);
        Assert.Contains("y70-infinite", _catalog.DeviceIds);
        Assert.Contains("y70-truly", _catalog.DeviceIds);
    }

    [Theory]
    [InlineData("np50", "2.0.5.1")]
    [InlineData("cnvs", "1.0.2.2")]
    [InlineData("fan-hub", "1.0.1.1")]
    [InlineData("q60", "2.0.9.1")]
    [InlineData("q80", "1.0.9.1")]
    [InlineData("y70-touch", "1.0.3.1")]
    [InlineData("y70-infinite", "1.0.3.1")]
    [InlineData("y70-truly", "1.0.3.1")]
    public void GetLatestVersion_returns_the_bundled_version(string deviceId, string expected)
    {
        Assert.Equal(expected, _catalog.GetLatestVersion(deviceId));
    }

    [Fact]
    public void GetLatestVersion_is_empty_for_unbundled_devices()
    {
        // The bare handler ids ("y70" / "qseries") have no bundle — they're the
        // "variant not yet identified" sentinels.
        Assert.Equal("", _catalog.GetLatestVersion("y70"));
        Assert.Equal("", _catalog.GetLatestVersion("qseries"));
        Assert.Equal("", _catalog.GetLatestVersion(""));
    }

    [Fact]
    public void OpenFirmware_returns_a_readable_stream_for_a_bundled_image()
    {
        using var stream = _catalog.OpenFirmware("np50", "2.0.5.1");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var firstLine = reader.ReadLine();
        // Intel HEX records start with ':'.
        Assert.StartsWith(":", firstLine);
    }

    [Fact]
    public void OpenFirmware_returns_null_for_a_missing_version()
    {
        Assert.Null(_catalog.OpenFirmware("np50", "9.9.9.9"));
    }

    [Theory]
    [InlineData("2.0.5.1", "2.0.0.1", true)]
    [InlineData("2.0.5.1", "1.9.9.9", true)]
    [InlineData("2.0.5.1", "2.0.5.1", false)]
    [InlineData("2.0.0.1", "2.0.5.1", false)]
    [InlineData("2.0.5.1", "", false)]
    [InlineData("", "2.0.5.1", false)]
    public void IsNewer_compares_dotted_versions(string available, string current, bool expected)
    {
        Assert.Equal(expected, BundledFirmwareCatalog.IsNewer(available, current));
    }
}
