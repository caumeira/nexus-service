using System.Text.Json;
using Nexus.Service.Serialization;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

/// <summary>Serializes through the real AppJsonContext, not a hand-written string, so a field rename is caught the moment it breaks the nexus-api contract.</summary>
public class FleetEventPayloadWireTests
{
    [Fact]
    public void Payload_without_specs_omits_the_specs_field()
    {
        var payload = new FleetEventPayload
        {
            InstallId = "abc123",
            Type = TelemetryEvents.Install,
            Version = "3.1.0",
            Os = "win",
            OsVersion = "Windows 11",
            Arch = "x64",
            DeviceType = "desktop",
        };

        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("abc123", root.GetProperty("installId").GetString());
        Assert.Equal("install", root.GetProperty("type").GetString());
        Assert.Equal("3.1.0", root.GetProperty("version").GetString());
        Assert.Equal("win", root.GetProperty("os").GetString());
        Assert.Equal("Windows 11", root.GetProperty("osVersion").GetString());
        Assert.Equal("x64", root.GetProperty("arch").GetString());
        Assert.Equal("desktop", root.GetProperty("deviceType").GetString());
        Assert.False(root.TryGetProperty("specs", out _));
    }

    [Fact]
    public void Specs_event_carries_the_coarse_hardware_summary()
    {
        var payload = new FleetEventPayload
        {
            InstallId = "abc123",
            Type = TelemetryEvents.Specs,
            Version = "3.1.0",
            Os = "win",
            OsVersion = "Windows 11",
            Arch = "x64",
            DeviceType = "desktop",
            Specs = new FleetEventSpecs
            {
                Cpu = "AMD Ryzen 9 9950X",
                Gpu = new[] { "NVIDIA RTX 5080" },
                RamBytes = 34359738368,
                Motherboard = "ASUS ROG X870E",
            },
        };

        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.FleetEventPayload);
        var specs = JsonDocument.Parse(json).RootElement.GetProperty("specs");

        Assert.Equal("AMD Ryzen 9 9950X", specs.GetProperty("cpu").GetString());
        Assert.Equal("NVIDIA RTX 5080", specs.GetProperty("gpu")[0].GetString());
        Assert.Equal(34359738368, specs.GetProperty("ramBytes").GetInt64());
        Assert.Equal("ASUS ROG X870E", specs.GetProperty("motherboard").GetString());
    }
}
