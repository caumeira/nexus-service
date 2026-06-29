namespace Nexus.Service.Lighting;

public sealed class StrimerModeInfo
{
    public string Key          { get; init; } = "";
    public string Label        { get; init; } = "";
    public byte   EffectByte   { get; init; }
    public bool   HasSpeed     { get; init; }
    public bool   HasDirection { get; init; }
    public bool   HasBrightness{ get; init; }
    public int    ColorsMin    { get; init; }
    public int    ColorsMax    { get; init; }
}

public static class StrimerLightingModes
{
    // Speed index 0..4 -> firmware byte. Slowest=0x02, fastest=0xFF.
    // Index 3 and 4 differ from SLI: Strimer uses 0xFE/0xFF, SLI uses 0xFF/0xFE.
    public static readonly byte[] SpeedCodes = { 0x02, 0x01, 0x00, 0xFE, 0xFF };

    // Brightness index 0..4 -> firmware byte. 0x08=off, 0x00=full (inverted scale).
    public static readonly byte[] BrightnessCodes = { 0x08, 0x03, 0x02, 0x01, 0x00 };

    // Direction wire byte is inverted vs SLI: 0=forward maps to 0x01, 1=reverse to 0x00.
    public static byte DirectionByte(int direction) => direction == 0 ? (byte)0x01 : (byte)0x00;

    public static readonly StrimerModeInfo[] Catalog =
    {
        new() { Key = "custom",        Label = "Custom (per-LED effects)", EffectByte = 0x01, HasSpeed = false, HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "off",           Label = "Off",                      EffectByte = 0x00, HasSpeed = false, HasDirection = false, HasBrightness = false, ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "breathing",     Label = "Breathing",                EffectByte = 0x02, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "flashing",      Label = "Flashing",                 EffectByte = 0x03, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "rainbowMorph",  Label = "Rainbow Morph",            EffectByte = 0x04, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "rainbow",       Label = "Rainbow",                  EffectByte = 0x05, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "spectrumCycle", Label = "Spectrum Cycle",           EffectByte = 0x06, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "snooker",       Label = "Snooker",                  EffectByte = 0x19, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "mixing",        Label = "Mixing",                   EffectByte = 0x1A, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "pingPong",      Label = "Ping Pong",                EffectByte = 0x1B, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "runway",        Label = "Runway",                   EffectByte = 0x1C, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "painting",      Label = "Painting",                 EffectByte = 0x1D, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "tide",          Label = "Tide",                     EffectByte = 0x1E, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "blowUp",        Label = "Blow Up",                  EffectByte = 0x1F, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "meteor",        Label = "Meteor",                   EffectByte = 0x20, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "shockWave",     Label = "Shock Wave",               EffectByte = 0x21, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "ripple",        Label = "Ripple",                   EffectByte = 0x22, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "voice",         Label = "Voice",                    EffectByte = 0x23, HasSpeed = false, HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "bulletStack",   Label = "Bullet Stack",             EffectByte = 0x24, HasSpeed = true,  HasDirection = true,  HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "drizzling",     Label = "Drizzling",                EffectByte = 0x25, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "fadeOut",       Label = "Fade Out",                 EffectByte = 0x26, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "colorTransfer", Label = "Color Transfer",           EffectByte = 0x27, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "crossOver",     Label = "Cross Over",               EffectByte = 0x28, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "twinkle",       Label = "Twinkle",                  EffectByte = 0x29, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "contest",       Label = "Contest",                  EffectByte = 0x2A, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "parallel",      Label = "Parallel",                 EffectByte = 0x2B, HasSpeed = true,  HasDirection = false, HasBrightness = true,  ColorsMin = 0, ColorsMax = 0 },
    };

    public static StrimerModeInfo? Find(string key)
    {
        foreach (var m in Catalog)
        {
            if (m.Key == key) return m;
        }
        return null;
    }
}
