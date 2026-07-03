using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>One sensor's display-ready reading: an already unit-converted value plus the gauge scale and labels.</summary>
public readonly record struct Slv3LcdSensorReading(float Value, float Min, float Max, string Label, string Unit);

/// <summary>
/// Reads one named live sensor value from <see cref="ISensorProvider"/> for
/// the LCD sensor content renderer, mirroring the CPU/GPU/motherboard sensor
/// lookup pattern in TryxPanoramaHub.ReadSensors. GPU sources read 0 on hosts
/// where LHM exposes no GPU sensors (a known service-wide limitation); CPU
/// load/temp and fan RPM are the reliable live values.
/// </summary>
public sealed class Slv3LcdSensorReader
{
    private const float FanRpmCeiling = 3000f;

    private readonly ISensorProvider _sensors;

    public Slv3LcdSensorReader(ISensorProvider sensors)
    {
        _sensors = sensors;
    }

    public Slv3LcdSensorReading Read(string? source, string? tempUnit)
    {
        var fahrenheit = string.Equals(tempUnit, "f", StringComparison.OrdinalIgnoreCase);
        return source switch
        {
            "cpuLoad" => new Slv3LcdSensorReading(FindSensor(_sensors.GetCpuSensors(), "Load", "CPU Total")?.Value ?? 0f, 0f, 100f, "CPU", "%"),
            "gpuLoad" => new Slv3LcdSensorReading(FindSensor(PrimaryGpuSensors(), "Load", null)?.Value ?? 0f, 0f, 100f, "GPU", "%"),
            "gpuTemp" => TempReading(FindSensor(PrimaryGpuSensors(), "Temperature", null)?.Value ?? 0f, "GPU", fahrenheit),
            "fanRpm" => FanReading(),
            // "cpuTemp" and any unrecognized/missing source default to CPU package temperature.
            _ => TempReading(FindSensor(_sensors.GetCpuSensors(), "Temperature", "Package")?.Value ?? 0f, "CPU", fahrenheit),
        };
    }

    private Slv3LcdSensorReading FanReading()
    {
        var mobo = _sensors.GetMotherboardSensors();
        var max = 0f;
        for (var i = 0; i < mobo.Count; i++)
        {
            if (string.Equals(mobo[i].Type, "Fan", StringComparison.OrdinalIgnoreCase) && mobo[i].Value > max)
            {
                max = mobo[i].Value;
            }
        }
        return new Slv3LcdSensorReading(max, 0f, FanRpmCeiling, "FAN", "RPM");
    }

    private static Slv3LcdSensorReading TempReading(float celsius, string label, bool fahrenheit)
    {
        if (!fahrenheit)
        {
            return new Slv3LcdSensorReading(celsius, 0f, 100f, label, "°C");
        }
        var fahrenheitValue = celsius * 9f / 5f + 32f;
        return new Slv3LcdSensorReading(fahrenheitValue, 32f, 212f, label, "°F");
    }

    private IReadOnlyList<HardwareSensor> PrimaryGpuSensors()
    {
        var gpus = _sensors.GetGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!gpus[i].Integrated)
            {
                return gpus[i].Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null || s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }
}
