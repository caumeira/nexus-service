using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Lighting;

public sealed class LianLiModeInfo
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public byte EffectByte { get; init; }
    public bool HasSpeed { get; init; }
    public bool HasDirection { get; init; }
    public bool HasBrightness { get; init; }
    public int ColorsMin { get; init; }
    public int ColorsMax { get; init; }

    /// <summary>True for effects 0x26..0x29, which the SL v1 firmware does not implement. SL-Infinity support is <see cref="SlInfinityEffectByte"/>.</summary>
    public bool V2Only { get; init; }

    /// <summary>Effect byte of the across-every-port variant; 0 when the mode has none.</summary>
    public byte MergedEffectByte { get; init; }

    /// <summary>SL-Infinity firmware's own effect numbering (fw 1.4); 0 when it has no equivalent.</summary>
    public byte SlInfinityEffectByte { get; init; }

    public bool SupportedBy(LianLiFanFamily family) => family == LianLiFanFamily.SlInfinity
        ? SlInfinityEffectByte != 0
        : !V2Only || family != LianLiFanFamily.Sl;

    public byte EffectByteFor(LianLiFanFamily family) =>
        family == LianLiFanFamily.SlInfinity ? SlInfinityEffectByte : EffectByte;

    public bool MergesOn(in LianLiFanProfile profile) => MergedEffectByte != 0 && profile.SupportsMerge;
}

public static class LianLiLightingModes
{
    // speed index 0..4 -> firmware byte; 0x02=slowest, 0xFE=fastest
    public static readonly byte[] SpeedCodes = { 0x02, 0x01, 0x00, 0xFF, 0xFE };

    // brightness index 0..4 -> firmware byte; 0x08=off, 0x00=full (inverted scale)
    public static readonly byte[] BrightnessCodes = { 0x08, 0x03, 0x02, 0x01, 0x00 };

    // direction 0=LTR(0x00), 1=RTL(0x01)
    public static byte DirectionByte(int direction) => direction == 1 ? (byte)0x01 : (byte)0x00;

    public static readonly LianLiModeInfo[] Catalog =
    {
        new() { Key = "custom",        Label = "Custom (per-LED effects)", EffectByte = 0x01, HasSpeed = false, HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0, SlInfinityEffectByte = 0x01 },
        new() { Key = "static",        Label = "Static",                   EffectByte = 0x01, HasSpeed = false, HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 6, SlInfinityEffectByte = 0x01 },
        new() { Key = "breathing",     Label = "Breathing",                EffectByte = 0x02, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 6, SlInfinityEffectByte = 0x02 },
        new() { Key = "spectrumCycle", Label = "Spectrum Cycle",           EffectByte = 0x04, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0, SlInfinityEffectByte = 0x04 },
        new() { Key = "rainbowWave",   Label = "Rainbow Wave",             EffectByte = 0x05, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 0, SlInfinityEffectByte = 0x05 },
        new() { Key = "staggered",     Label = "Staggered",                EffectByte = 0x18, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "tide",          Label = "Tide",                     EffectByte = 0x1A, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2, MergedEffectByte = 0x4A, SlInfinityEffectByte = 0x3A },
        new() { Key = "runway",        Label = "Runway",                   EffectByte = 0x1C, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2, MergedEffectByte = 0x46, SlInfinityEffectByte = 0x1A },
        new() { Key = "mixing",        Label = "Mixing",                   EffectByte = 0x1E, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2, MergedEffectByte = 0x48, SlInfinityEffectByte = 0x38 },
        new() { Key = "stack",         Label = "Stack",                    EffectByte = 0x20, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 1, MergedEffectByte = 0x49, SlInfinityEffectByte = 0x39 },
        new() { Key = "neon",          Label = "Neon",                     EffectByte = 0x22, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "colorCycle",    Label = "Color Cycle",              EffectByte = 0x23, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 3, SlInfinityEffectByte = 0x18 },
        new() { Key = "meteor",        Label = "Meteor",                   EffectByte = 0x24, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2, SlInfinityEffectByte = 0x19 },
        new() { Key = "voice",         Label = "Voice",                    EffectByte = 0x26, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0, V2Only = true, SlInfinityEffectByte = 0x2A },
        new() { Key = "groove",          Label = "Groove",            EffectByte = 0x27, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 2, V2Only = true },
        new() { Key = "stackMultiColor", Label = "Stack Multi Color", EffectByte = 0x21, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "render",          Label = "Render",            EffectByte = 0x28, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 4, V2Only = true },
        new() { Key = "tunnel",          Label = "Tunnel",            EffectByte = 0x29, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 4, V2Only = true },
    };

    /// <summary>The catalog entries a family's firmware accepts, in catalog order.</summary>
    public static IReadOnlyList<LianLiModeInfo> CatalogFor(LianLiFanFamily family)
    {
        var list = new List<LianLiModeInfo>(Catalog.Length);
        foreach (var m in Catalog)
        {
            if (m.SupportedBy(family)) list.Add(m);
        }
        return list;
    }

    public static LianLiModeInfo? Find(string key)
    {
        foreach (var m in Catalog)
        {
            if (m.Key == key)
            {
                return m;
            }
        }
        return null;
    }
}
