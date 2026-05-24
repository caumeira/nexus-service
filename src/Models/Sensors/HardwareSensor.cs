using System.Collections.Generic;

namespace Nexus.Service.Models.Sensors;

/// <summary>
/// Single sensor reading matching the ISystemSensor JSON shape.
/// `Type` is a string (LibreHardwareMonitor sensor type names: Load, Temperature,
/// Clock, Power, Voltage, Fan, etc) so the SPA can render any sensor without
/// shipping the enum.
/// </summary>
public class HardwareSensor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Load";
    public float Value { get; set; }
    public float Min { get; set; }
    public float Max { get; set; }
    public float Average { get; set; }
    public float Usage { get; set; }
    public float TheoreticalMaximum { get; set; }
    public string Units { get; set; } = "";
    public string Formatted { get; set; } = "";
    public string FormattedMax { get; set; } = "";
    public string FormattedMin { get; set; } = "";
    public string FormattedAverage { get; set; } = "";
    public string FormattedUsage { get; set; } = "";
    public SensorParent Parent { get; set; } = new();
}

public class SensorParent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class HardwareComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<HardwareSensor> Sensors { get; set; } = new();
}

public class StorageComponent : HardwareComponent
{
    public string Format { get; set; } = "";
    public string Capacity { get; set; } = "";
    public string FreeSpace { get; set; } = "";
    public string UsedSpace { get; set; } = "";
    public string UsedPercentage { get; set; } = "";
}

public class StorageDriveInfo
{
    public string Name { get; set; } = "";
    public string Partition { get; set; } = "";
    public string Capacity { get; set; } = "";
}
