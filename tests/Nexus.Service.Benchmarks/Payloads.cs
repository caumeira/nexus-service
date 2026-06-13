using System.Collections.Generic;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;

namespace Nexus.Service.Benchmarks;

/// <summary>
/// Representative payloads sized to a typical desktop: a multi-core CPU, two
/// GPUs, memory, two drives, and a motherboard fan/voltage set. Built once and
/// reused so the benchmark measures serialization, not construction.
/// </summary>
internal static class Payloads
{
    public static HardwareComponent Component(string id, string name, int sensorCount, string? vendor = null)
    {
        var sensors = new List<HardwareSensor>(sensorCount);
        for (int i = 0; i < sensorCount; i++)
        {
            sensors.Add(new HardwareSensor
            {
                Id = $"{id}.s{i}",
                Name = $"{name} Sensor {i}",
                Type = (i % 3) switch { 0 => "Load", 1 => "Temperature", _ => "Clock" },
                Value = 42.5f + i,
                Min = 10f,
                Max = 99f,
                Average = 50f,
                Usage = 0.5f + i,
                Units = (i % 3) == 1 ? "°C" : (i % 3) == 0 ? "%" : "MHz",
                Formatted = $"{42 + i} units",
                Parent = new SensorParent { Id = id, Name = name },
            });
        }
        return new HardwareComponent { Id = id, Name = name, Vendor = vendor, Sensors = sensors };
    }

    public static MonitoringFrame MonitoringFrame()
    {
        var gpu = new List<HardwareComponent>
        {
            Component("gpu0", "NVIDIA GeForce RTX 5080", 12, "nvidia"),
            Component("gpu1", "AMD Radeon Graphics", 8, "amd"),
        };
        var storage = new Dictionary<string, StorageComponent>
        {
            ["disk0"] = new StorageComponent { Id = "disk0", Name = "Samsung 990 Pro", Format = "NVMe", Capacity = "2 TB", FreeSpace = "1.1 TB", UsedSpace = "0.9 TB", UsedPercentage = "45%", Sensors = Component("disk0", "Samsung 990 Pro", 4).Sensors },
            ["disk1"] = new StorageComponent { Id = "disk1", Name = "WD Blue", Format = "SATA", Capacity = "4 TB", FreeSpace = "2 TB", UsedSpace = "2 TB", UsedPercentage = "50%", Sensors = Component("disk1", "WD Blue", 4).Sensors },
        };
        return new MonitoringFrame
        {
            Cpu = Component("cpu", "AMD Ryzen 9 9950X", 32),
            Gpu = gpu,
            Memory = Component("mem", "Memory", 4),
            Storage = storage,
            Motherboard = Component("mb", "X670E", 10),
            CpuModel = "AMD Ryzen 9 9950X",
            GpuModels = new List<string> { "NVIDIA GeForce RTX 5080", "AMD Radeon Graphics" },
            MemoryTotal = "64 GB",
            MotherboardModel = "X670E",
        };
    }

    public static GraphCurveData GraphCurve(int points)
    {
        var data = new GraphCurveData { SpeedModifier = 1.0, ResponseTime = 1.0 };
        for (int i = 0; i < points; i++)
        {
            data.Points.Add(new GraphPoint { Temp = 20 + i * (60.0 / points), Speed = 20 + i * (80.0 / points) });
        }
        return data;
    }
}
