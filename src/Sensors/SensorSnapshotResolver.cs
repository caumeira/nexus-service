using System.Collections.Generic;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Sensors;

/// <summary>
/// Resolves a single sensor by (category, sensorId) over an ISensorProvider
/// snapshot, sharing the category vocabulary the Tryx overlay picker and the
/// deck monitoring picker both use (a superset - "quick" is deck-only,
/// "network" is Tryx-only per each caller's own category list). "gpu" scans
/// every GPU's sensors since GPU sensor ids are unique across GPUs on a
/// single machine, so a linear scan across all of them is correct.
/// </summary>
public static class SensorSnapshotResolver
{
    public static HardwareSensor? Resolve(ISensorProvider sensors, string category, string sensorId)
    {
        var list = GetCategorySensors(sensors, category);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Id == sensorId)
            {
                return list[i];
            }
        }
        return null;
    }

    public static IReadOnlyList<HardwareSensor> GetCategorySensors(ISensorProvider sensors, string category) => category switch
    {
        "quick" => SummarySensors.Build(sensors),
        "cpu" => sensors.GetCpuSensors(),
        "gpu" => FlattenGpus(sensors.GetGpus()),
        "memory" => sensors.GetMemorySensors(),
        "motherboard" => sensors.GetMotherboardSensors(),
        // includeSmart: false matches the Tryx overlay, the only caller of the
        // "storage" category today - it only ever resolved the DriveInfo
        // logical-volume Used/Free/Usage subset, never the LHM SMART rows.
        "storage" => FlattenComponents(sensors.GetStorageComponents(includeSmart: false).Values),
        "network" => FlattenComponents(sensors.GetSensorExtras().Nics),
        _ => System.Array.Empty<HardwareSensor>(),
    };

    private static IReadOnlyList<HardwareSensor> FlattenGpus(IReadOnlyList<GpuReadout> gpus)
    {
        if (gpus.Count == 0)
        {
            return System.Array.Empty<HardwareSensor>();
        }
        if (gpus.Count == 1)
        {
            return gpus[0].Sensors;
        }
        var list = new List<HardwareSensor>();
        foreach (var gpu in gpus)
        {
            list.AddRange(gpu.Sensors);
        }
        return list;
    }

    private static IReadOnlyList<HardwareSensor> FlattenComponents(IEnumerable<HardwareComponent> components)
    {
        var list = new List<HardwareSensor>();
        foreach (var component in components)
        {
            list.AddRange(component.Sensors);
        }
        return list;
    }
}
