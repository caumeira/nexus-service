using System;
using System.Collections.Generic;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Derives an AIB (add-in-board partner) brand from an NVIDIA GPU's PCI
/// subsystem vendor id (the low 16 bits of subSystemId from
/// NvAPI_GPU_GetPCIIdentifiers) and enriches LibreHardwareMonitor's raw NVAPI
/// model name with it. LHM's name is always "NVIDIA &lt;model&gt;" (see
/// NvidiaGpu.cs GetName); this replaces that generic prefix with the AIB's
/// brand so the client shows e.g. "ASUS GeForce RTX 5080" instead of "NVIDIA
/// GeForce RTX 5080".
/// </summary>
internal static class AstralAibVendors
{
    public const uint AsusVendorId = 0x1043;
    private const uint NvidiaVendorId = 0x10DE;

    private static readonly IReadOnlyDictionary<uint, string> Brands = new Dictionary<uint, string>
    {
        [0x1043] = "ASUS",
        [0x1462] = "MSI",
        [0x1458] = "Gigabyte",
        [0x3842] = "EVGA",
        [0x19DA] = "Zotac",
        [0x1569] = "Palit",
        [0x10B0] = "Gainward",
        [0x196E] = "PNY",
        [0x1DA2] = "Sapphire",
    };

    /// <summary>
    /// True with the brand name for a known AIB vendor id. False for NVIDIA's
    /// own Founders Edition vendor id and for unrecognized ids - callers should
    /// leave the name unchanged rather than guess a brand.
    /// </summary>
    public static bool TryGetBrand(uint subSystemId, out string brand)
    {
        var vendorId = subSystemId & 0xFFFF;
        if (vendorId == NvidiaVendorId)
        {
            brand = "";
            return false;
        }
        return Brands.TryGetValue(vendorId, out brand!);
    }

    /// <summary>
    /// Replaces the "NVIDIA" prefix LHM's raw model name carries with the AIB
    /// brand. When <paramref name="isAstral"/> the ASUS Astral controller
    /// answered the 12VHPWR telemetry probe, so the line-specific "ROG Astral"
    /// naming ASUS itself uses (no "GeForce") is used instead of the generic
    /// brand prefix. Unrecognized or Founders Edition vendors return the name
    /// unchanged.
    /// </summary>
    public static string EnrichName(string lhmName, uint subSystemId, bool isAstral)
    {
        var trimmed = lhmName.Trim();
        var model = StripLeadingWord(trimmed, "NVIDIA");

        if (isAstral)
        {
            return $"ASUS ROG Astral {StripLeadingWord(model, "GeForce")}".TrimEnd();
        }

        return TryGetBrand(subSystemId, out var brand) ? $"{brand} {model}".TrimEnd() : trimmed;
    }

    private static string StripLeadingWord(string name, string word)
    {
        if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return name[word.Length..].TrimStart();
        }
        return name;
    }
}
