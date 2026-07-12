using Nexus.Service.Deck;
using Nexus.Service.Models.Sensors;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Pins DeckMonitoringFormat's parity with nexus-web's DeckMonitoringCell:
/// labelForDevice (MonitoringWidget.tsx) for the default top label, and
/// formatScaledDataValue (sensorValueFormat.ts) for the byte-unit ladder the
/// device tile previously did not apply.
/// </summary>
public class DeckMonitoringFormatTests
{
    private static HardwareSensor Sensor(string name, float value, string units, string formatted) => new()
    {
        Id = "test/sensor",
        Name = name,
        Value = value,
        Units = units,
        Formatted = formatted,
        Parent = new SensorParent(),
    };

    [Theory]
    [InlineData("cpu", "CPU Total", "CPU Total")]
    [InlineData("cpu", "Total", "CPU Total")]
    [InlineData("cpu", "CPU", "CPU")]
    [InlineData("gpu", "Core Clock", "GPU Core Clock")]
    [InlineData("gpu", "GPU Core Clock", "GPU Core Clock")]
    [InlineData("memory", "Used", "Memory Used")]
    [InlineData("memory", "Memory Used", "Memory Used")]
    [InlineData("motherboard", "System", "System")]
    [InlineData("storage", "Drive C", "Drive C")]
    [InlineData("quick", "Anything", "Anything")]
    public void ResolveLabel_SensorNameSet_MirrorsLabelForDeviceAndPrefixedSensorLabel(string category, string sensorName, string expected)
    {
        Assert.Equal(expected, DeckMonitoringFormat.ResolveLabel(category, sensorName));
    }

    [Theory]
    [InlineData("quick", "Quick")]
    [InlineData("cpu", "CPU")]
    [InlineData("gpu", "GPU")]
    [InlineData("memory", "RAM")]
    [InlineData("motherboard", "MB")]
    [InlineData("storage", "Storage")]
    public void ResolveLabel_NoSensorName_FallsBackToTheCategoryDisplayName(string category, string expected)
    {
        Assert.Equal(expected, DeckMonitoringFormat.ResolveLabel(category, ""));
    }

    /// <summary>A Data-type sensor whose raw GB value crosses the 1024 boundary scales up to TB, matching the web cell.</summary>
    [Fact]
    public void ResolveValueText_LargeGbValue_ScalesUpToTb()
    {
        var sensor = Sensor("Used", 1214.96f, "GB", "1214.96 GB");
        Assert.Equal("1.2 TB", DeckMonitoringFormat.ResolveValueText(sensor));
    }

    [Fact]
    public void ResolveValueText_SmallGbValue_StaysInGbWithOneDecimal()
    {
        var sensor = Sensor("Used", 4.5f, "GB", "4.50 GB");
        Assert.Equal("4.5 GB", DeckMonitoringFormat.ResolveValueText(sensor));
    }

    [Fact]
    public void ResolveValueText_IntegerScaledValue_DropsTheDecimal()
    {
        var sensor = Sensor("Used", 2048f, "MB", "2048.00 MB");
        Assert.Equal("2 GB", DeckMonitoringFormat.ResolveValueText(sensor));
    }

    [Fact]
    public void ResolveValueText_Percent_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Total", 42f, "%", "42.0%");
        Assert.Equal("42.0%", DeckMonitoringFormat.ResolveValueText(sensor));
    }

    [Fact]
    public void ResolveValueText_Temperature_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Core", 65.3f, "°C", "65.3 °C");
        Assert.Equal("65.3 °C", DeckMonitoringFormat.ResolveValueText(sensor));
    }

    [Fact]
    public void ResolveValueText_Rpm_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Fan 1", 1200f, "RPM", "1200 RPM");
        Assert.Equal("1200 RPM", DeckMonitoringFormat.ResolveValueText(sensor));
    }

    [Fact]
    public void ResolveValueText_UnknownUnit_FallsBackToTheServiceFormattedString()
    {
        var sensor = Sensor("Clock", 4200f, "MHz", "4200 MHz");
        Assert.Equal("4200 MHz", DeckMonitoringFormat.ResolveValueText(sensor));
    }
}
