using System;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Authored zone structure for the HYTE Keeb TKL: two fixed segments in
/// device order, keys then underglow, with counts and stock per-LED
/// positions from the firmware layout (the key matrix decoded from the wire
/// values; the underglow as a clockwise board-edge perimeter). Both are
/// fixed (the firmware never re-wires LED counts), so any user partition
/// over them is index-stable.
/// </summary>
public static class KeebZoneSupport
{
    public const int KeysSegment = 0;
    public const int UnderglowSegment = 1;

    private static readonly Lazy<(float[] U, float[] V)> KeyUv = new(KeebLayout.ComputeKeyUv);

    private static readonly Lazy<(float[] U, float[] V)> UnderglowUv = new(() =>
    {
        var u = new float[KeebLayout.SurroundLedCount];
        var v = new float[KeebLayout.SurroundLedCount];
        // The underglow strip physically closes on itself; closed-loop
        // spacing keeps the seam LEDs from sharing a seed position.
        LedUvComputer.FillPerimeter(u, v, closedLoop: true);
        return (u, v);
    });

    public static DeviceStructure BuildStructure(string hubId)
    {
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
            LedCount = KeebLayout.KeyLedCount,
            FrameLedCount = KeebLayout.KeyLedCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = KeyUv.Value.U,
            DefaultV = KeyUv.Value.V,
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
            Slices = { new ZoneSlice { Segment = KeysSegment, Start = 0, Count = KeebLayout.KeyLedCount } },
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
