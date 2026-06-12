using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Devices;

// ----- /devices/lighting-devices/{deviceId}/structure -----

public sealed class StructureSegmentDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int LedCount { get; set; }
    /// <summary>Resizable segments are walls: a zone touching one must be exactly that whole segment.</summary>
    public bool Resizable { get; set; }
    public string ZoneType { get; set; } = "";
}

public sealed class StructureZoneDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ZoneSlice> Slices { get; set; } = new();
}

public sealed class DeviceStructureResponse : ApiResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public List<StructureSegmentDto> Segments { get; set; } = new();
    public List<StructureZoneDto> Zones { get; set; } = new();
    public bool IsDefaultPartition { get; set; }
}

// ----- /devices/lighting-devices/{deviceId}/zones -----

public sealed class SaveZonePartitionBody
{
    public List<ZoneDef> Zones { get; set; } = new();
}

// ----- /devices/lighting-devices/{deviceId}/device-map -----

public sealed class DeviceMapLedDto
{
    /// <summary>Segment-local LED index.</summary>
    public int Index { get; set; }
    public float U { get; set; }
    public float V { get; set; }
    public string Name { get; set; } = "";
    /// <summary>True when a user override exists for this LED.</summary>
    public bool IsCustom { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Card id of the zone this LED belongs to.</summary>
    public string ZoneId { get; set; } = "";
}

public sealed class DeviceMapSegmentDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int LedCount { get; set; }
    public bool Resizable { get; set; }
    public string ZoneType { get; set; } = "";
    public List<DeviceMapLedDto> Leds { get; set; } = new();
}

public sealed class DeviceMapResponse : ApiResponse
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefaultPartition { get; set; }
    public float AspectRatio { get; set; }
    public List<DeviceMapSegmentDto> Segments { get; set; } = new();
}

public sealed class SaveDeviceMapBody
{
    /// <summary>Segment-local override list; replaces the device's stored overrides wholesale.</summary>
    public List<SegmentLedOverride> Overrides { get; set; } = new();
    /// <summary>Editor canvas aspect ratio; non-positive leaves the stored value untouched.</summary>
    public float AspectRatio { get; set; }
}
