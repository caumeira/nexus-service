using System;
using System.Collections.Concurrent;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Authored zone structure for the HYTE Keeb TKL: two fixed segments in
/// device order, keys then underglow, with counts and stock per-LED
/// positions from the firmware board-grid tables - keys from the layout's
/// <see cref="KeebKeyMap"/>, the underglow from <see cref="KeebLayout.SurroundGrid"/>,
/// both normalized on the one shared grid. Counts are fixed per layout (the
/// firmware never re-wires them), so any user partition over them is
/// index-stable for as long as the board stays the same.
/// </summary>
public static class KeebZoneSupport
{
    public const int KeysSegment = 0;
    public const int UnderglowSegment = 1;

    // Key UV depends on the board layout, so cache one per map rather than one
    // for the type. Two entries, ever.
    private static readonly ConcurrentDictionary<KeebKeyMap, (float[] U, float[] V)> KeyUv = new();

    // The underglow strip is NOT an evenly-spaced ring: it is seamed at top
    // centre, runs counter-clockwise, and its last LED is the scroll wheel,
    // off the perimeter entirely. A generic FillPerimeter walk cannot express
    // any of that, so the positions come from the firmware layout table.
    private static readonly Lazy<(float[] U, float[] V)> UnderglowUv = new(KeebLayout.ComputeSurroundUv);

    public static DeviceStructure BuildStructure(string hubId, KeebKeyMap keys)
    {
        var keyUv = KeyUv.GetOrAdd(keys, static m => m.ComputeUv());
        var structure = new DeviceStructure
        {
            DeviceId = hubId,
            Name = KeebHub.ProductName,
            DeviceKey = DeviceKeyComputer.ForFirstParty(KeebProtocol.VendorId, KeebProtocol.ProductId),
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = KeysSegment,
            Name = "Keys",
            LedCount = keys.LedCount,
            FrameLedCount = keys.LedCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = keyUv.U,
            DefaultV = keyUv.V,
        });
        structure.Segments.Add(new StructureSegment
        {
            Index = UnderglowSegment,
            Name = "Underglow",
            LedCount = KeebLayout.SurroundLedCount,
            FrameLedCount = KeebLayout.SurroundLedCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = UnderglowUv.Value.U,
            DefaultV = UnderglowUv.Value.V,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = hubId + KeebLightingDeviceProvider.KeysSuffix,
            Name = $"{KeebHub.ProductName} - Keys",
            RawName = structure.Segments[KeysSegment].Name,
            DeviceKey = DeviceKeyComputer.ForFirstParty(KeebProtocol.VendorId, KeebProtocol.ProductId, "keys"),
            LegacyZoneIndex = 0,
            Slices = { new ZoneSlice { Segment = KeysSegment, Start = 0, Count = keys.LedCount } },
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = hubId + KeebLightingDeviceProvider.UnderglowSuffix,
            Name = $"{KeebHub.ProductName} - Underglow",
            RawName = structure.Segments[UnderglowSegment].Name,
            DeviceKey = DeviceKeyComputer.ForFirstParty(KeebProtocol.VendorId, KeebProtocol.ProductId, "underglow"),
            LegacyZoneIndex = 1,
            Slices = { new ZoneSlice { Segment = UnderglowSegment, Start = 0, Count = KeebLayout.SurroundLedCount } },
        });
        return structure;
    }
}
