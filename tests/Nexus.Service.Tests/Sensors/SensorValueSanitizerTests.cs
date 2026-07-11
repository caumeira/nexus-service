using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Sensors;

public class SensorValueSanitizerTests
{
    [Theory]
    [InlineData(72.5f, 72.5f)]
    [InlineData(0f, 0f)]
    [InlineData(-40f, -40f)]
    [InlineData(float.MaxValue, float.MaxValue)]
    [InlineData(float.MinValue, float.MinValue)]
    public void Sanitize_passes_finite_values_through(float input, float expected)
    {
        Assert.Equal(expected, SensorValueSanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Sanitize_clamps_non_finite_values_to_zero(float input)
    {
        Assert.Equal(0f, SensorValueSanitizer.Sanitize(input));
    }

    // Reproduces the Y70 runtime failure at the DTO + serializer boundary: an
    // LHM DIMM SPD sensor reported Infinity, which made System.Text.Json throw
    // building the "extras" envelope and killed the whole topic (nvmeStorage,
    // batteries, nics, coolers, psus, embeddedControllers, memoryModules) for
    // every subscriber. Sanitizing before the value reaches the DTO keeps the
    // AOT source-gen serializer from ever seeing a non-finite float.
    [Fact]
    public void SensorExtras_with_sanitized_dimm_value_serializes_without_throwing()
    {
        var extras = new SensorExtras();
        extras.MemoryModules.Add(new HardwareComponent
        {
            Id = "/memory/dimm/0",
            Name = "DIMM 0",
            Sensors = new List<HardwareSensor>
            {
                new()
                {
                    Id = "/memory/dimm/0/temperature/0",
                    Name = "SPD Temperature",
                    Type = "Temperature",
                    Value = SensorValueSanitizer.Sanitize(float.PositiveInfinity),
                    Min = SensorValueSanitizer.Sanitize(float.NaN),
                    Max = SensorValueSanitizer.Sanitize(float.NegativeInfinity),
                    Units = "°C",
                    Formatted = "0.0 °C",
                    Parent = new SensorParent { Id = "/memory/dimm/0", Name = "DIMM 0" },
                },
            },
        });

        var envelope = WsEnvelope.Build("extras", extras, AppJsonContext.Default.SensorExtras);
        var json = Encoding.UTF8.GetString(envelope.Span);

        using var doc = JsonDocument.Parse(json);
        var sensor = doc.RootElement.GetProperty("d").GetProperty("memoryModules")[0].GetProperty("sensors")[0];
        Assert.Equal(0f, sensor.GetProperty("value").GetSingle());
        Assert.Equal(0f, sensor.GetProperty("min").GetSingle());
        Assert.Equal(0f, sensor.GetProperty("max").GetSingle());
    }

    // Proves the failure mode this fix closes: an unsanitized non-finite float
    // reaching a HardwareSensor DTO makes the source-gen serializer throw with
    // the exact message observed on the Y70 ("cannot be written as valid
    // JSON"), confirming the sanitizer above is load-bearing, not incidental.
    [Fact]
    public void SensorExtras_with_raw_nonfinite_value_throws()
    {
        var extras = new SensorExtras();
        extras.MemoryModules.Add(new HardwareComponent
        {
            Id = "/memory/dimm/0",
            Name = "DIMM 0",
            Sensors = new List<HardwareSensor>
            {
                new()
                {
                    Id = "/memory/dimm/0/temperature/0",
                    Name = "SPD Temperature",
                    Type = "Temperature",
                    Value = float.PositiveInfinity,
                    Parent = new SensorParent { Id = "/memory/dimm/0", Name = "DIMM 0" },
                },
            },
        });

        Assert.Throws<ArgumentException>(() =>
            WsEnvelope.Build("extras", extras, AppJsonContext.Default.SensorExtras));
    }
}
