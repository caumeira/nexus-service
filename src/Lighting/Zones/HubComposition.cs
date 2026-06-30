using System;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Zones;

/// <summary>
/// One logical lighting device produced by a hub's channel composition: a
/// normal partitionable <see cref="DeviceStructure"/> plus, per segment, the
/// physical hardware channels that segment streams to. More than one channel
/// for a segment is a mirror broadcast - the same rendered LEDs go to every
/// listed channel. Composition decides the device set and these channel
/// bindings; the user still re-zones each device through the partition system.
/// </summary>
public sealed class ComposedDevice
{
    public ComposedDevice(DeviceStructure structure, IReadOnlyList<IReadOnlyList<int>> segmentChannels)
    {
        Structure = structure;
        SegmentChannels = segmentChannels;
    }

    public DeviceStructure Structure { get; }

    /// <summary>Indexed by segment index; each entry lists the hardware channels that segment drives.</summary>
    public IReadOnlyList<IReadOnlyList<int>> SegmentChannels { get; }
}

/// <summary>Composition capability and current state for one hub, surfaced to the LED-map editor.</summary>
public sealed class HubCompositionInfo
{
    public string HubId { get; set; } = "";
    /// <summary>"lianli" | "smarthub" - selects the web setter endpoint.</summary>
    public string HubKind { get; set; } = "";
    public int PortCount { get; set; }
    /// <summary>Hub has an inner/outer ring axis (the combine toggle applies).</summary>
    public bool HasRingsAxis { get; set; }
    /// <summary>Ports can be individually enabled/disabled (the port chips apply).</summary>
    public bool HasPortToggle { get; set; }
    /// <summary>Hub supports mirror-all-ports broadcast (the mirror toggle applies).</summary>
    public bool HasMirror { get; set; }
    public bool Mirror { get; set; }
    public bool CombineRings { get; set; }
    /// <summary>Length <see cref="PortCount"/>; which ports currently contribute a device.</summary>
    public bool[] ActivePorts { get; set; } = Array.Empty<bool>();
}

/// <summary>A hub whose physical channels compose into a user-configurable device set.</summary>
public interface IComposableHubSource
{
    /// <summary>Composition info when <paramref name="deviceId"/> belongs to this hub; null otherwise.</summary>
    HubCompositionInfo? DescribeComposition(string deviceId);
}
