using System.Collections.Generic;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Monitoring;

/// <summary>
/// Projects first-party cooling-hub temperature probes (NP50 cable + per-FP12, Q-series coolant,
/// iCUE LINK fan probes) into the monitoring "extras" shape so they appear in the widget sensor
/// picker's Cooler category. The cooling providers own these probes; the platform sensor providers
/// only ever see coolers LibreHardwareMonitor recognises, so without this the two lists disagree.
/// </summary>
internal static class HubCoolerSensors
{
    /// <summary>
    /// Group every device-owned temperature source by its device. Sources with no DeviceId are
    /// motherboard/CPU/GPU channels that the platform provider already reports, and are skipped.
    /// Ids match the curve-source ids, so a probe is the same sensor in both pickers.
    /// </summary>
    public static List<HardwareComponent> Build(IReadOnlyList<TemperatureSource> sources)
    {
        var byDevice = new Dictionary<string, HardwareComponent>();
        var ordered = new List<HardwareComponent>();

        foreach (var s in sources)
        {
            if (string.IsNullOrEmpty(s.DeviceId)) continue;
            // A non-finite float fails serialization of the whole extras envelope, not just this row.
            if (!float.IsFinite(s.Value)) continue;

            if (!byDevice.TryGetValue(s.DeviceId, out var component))
            {
                component = new HardwareComponent
                {
                    Id = s.DeviceId,
                    Name = string.IsNullOrEmpty(s.DeviceName) ? s.DeviceId : s.DeviceName,
                };
                byDevice[s.DeviceId] = component;
                ordered.Add(component);
            }

            component.Sensors.Add(new HardwareSensor
            {
                Id = s.Id,
                Name = s.Name,
                Type = "Temperature",
                Value = s.Value,
                Min = s.Value,
                Max = s.Value,
                Units = "°C",
                Formatted = $"{s.Value:F1} °C",
                FormattedMin = $"{s.Value:F1} °C",
                FormattedMax = $"{s.Value:F1} °C",
                Parent = new SensorParent { Id = component.Id, Name = component.Name },
            });
        }

        return ordered;
    }
}
