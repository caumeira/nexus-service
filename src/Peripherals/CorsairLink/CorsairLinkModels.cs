using System.Collections.Generic;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>Broad capability class of a connected LINK device, derived from its (type, model).</summary>
public enum CorsairLinkClass
{
    Other,
    Fan,
    Aio,
    Pump,
    CpuBlock,
    GpuBlock,
    Case,
    Adapter,
}

/// <summary>Static metadata for one connected device model.</summary>
public sealed class CorsairLinkModel
{
    public string Name { get; init; } = "iCUE LINK Device";
    public int LedCount { get; init; }
    public CorsairLinkClass Class { get; init; } = CorsairLinkClass.Other;

    /// <summary>Reports RPM and accepts a duty (fans, AIO/standalone pumps).</summary>
    public bool HasSpeed { get; init; }

    /// <summary>Carries a temperature probe (QX fans, liquid loops, CPU/pump blocks).</summary>
    public bool HasTemperature { get; init; }
}

/// <summary>
/// Maps the (type, model) bytes the hub returns in its device enumeration to a
/// human name, LED count, and capability class. Table merged from OpenLinkHub
/// (database/external/lsh.json) and OpenRGB (CorsairICueLinkProtocol.h). LED
/// counts are the static per-model values both projects use for known devices;
/// the hub's dynamic LED-count read returns 0 for these, so the table is
/// authoritative.
/// </summary>
public static class CorsairLinkModels
{
    private static readonly Dictionary<(int type, int model), CorsairLinkModel> Table = new()
    {
        [(1, 0)] = new() { Name = "iCUE LINK QX RGB", LedCount = 34, Class = CorsairLinkClass.Fan, HasSpeed = true, HasTemperature = true },
        [(2, 0)] = new() { Name = "iCUE LINK LX RGB", LedCount = 18, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(3, 0)] = new() { Name = "iCUE LINK RX RGB MAX", LedCount = 8, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(4, 0)] = new() { Name = "iCUE LINK RX MAX", LedCount = 0, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(5, 0)] = new() { Name = "iCUE LINK Adapter", LedCount = 0, Class = CorsairLinkClass.Adapter },
        [(5, 1)] = new() { Name = "iCUE LINK 9000D Airflow", LedCount = 22, Class = CorsairLinkClass.Case },
        [(5, 2)] = new() { Name = "iCUE LINK 5000T", LedCount = 160, Class = CorsairLinkClass.Case },
        [(6, 0)] = new() { Name = "iCUE LINK Cooler Pump LCD", LedCount = 24, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(7, 0)] = new() { Name = "iCUE LINK H100i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(7, 1)] = new() { Name = "iCUE LINK H115i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(7, 2)] = new() { Name = "iCUE LINK H150i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(7, 3)] = new() { Name = "iCUE LINK H170i", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(9, 0)] = new() { Name = "iCUE LINK XC7 Elite", LedCount = 24, Class = CorsairLinkClass.CpuBlock, HasTemperature = true },
        [(9, 1)] = new() { Name = "iCUE LINK XC7 Elite", LedCount = 24, Class = CorsairLinkClass.CpuBlock, HasTemperature = true },
        [(10, 0)] = new() { Name = "iCUE LINK XG3 Hybrid", LedCount = 0, Class = CorsairLinkClass.GpuBlock, HasSpeed = true },
        [(12, 0)] = new() { Name = "iCUE LINK XD5 Elite", LedCount = 22, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(13, 0)] = new() { Name = "iCUE LINK XG7 RGB", LedCount = 16, Class = CorsairLinkClass.GpuBlock },
        [(14, 0)] = new() { Name = "iCUE LINK XD5 Elite LCD", LedCount = 22, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(15, 0)] = new() { Name = "iCUE LINK RX RGB", LedCount = 8, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(16, 0)] = new() { Name = "VRM Cooler Module", LedCount = 0, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(17, 0)] = new() { Name = "iCUE LINK Titan 240", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 1)] = new() { Name = "iCUE LINK Titan 280", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 2)] = new() { Name = "iCUE LINK Titan 360", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 3)] = new() { Name = "iCUE LINK Titan 420", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 4)] = new() { Name = "iCUE LINK Titan 240", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(17, 5)] = new() { Name = "iCUE LINK Titan 360", LedCount = 20, Class = CorsairLinkClass.Aio, HasSpeed = true, HasTemperature = true },
        [(19, 0)] = new() { Name = "iCUE LINK RX", LedCount = 0, Class = CorsairLinkClass.Fan, HasSpeed = true },
        [(25, 0)] = new() { Name = "iCUE LINK XD6 Elite", LedCount = 22, Class = CorsairLinkClass.Pump, HasSpeed = true, HasTemperature = true },
        [(27, 0)] = new() { Name = "iCUE Commander Duo", LedCount = 0, Class = CorsairLinkClass.Adapter, HasTemperature = true },
    };

    public static CorsairLinkModel Lookup(int type, int model)
    {
        if (Table.TryGetValue((type, model), out var m)) return m;
        // Unknown model of a known type: fall back to model 0 so a new revision
        // still surfaces with the right name/class rather than as fully unknown.
        if (Table.TryGetValue((type, 0), out var baseM)) return baseM;
        return new CorsairLinkModel { Name = $"iCUE LINK Device {type}.{model}", Class = CorsairLinkClass.Other };
    }
}
