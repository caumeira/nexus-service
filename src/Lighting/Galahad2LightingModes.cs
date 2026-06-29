namespace Nexus.Service.Lighting;

public sealed class Galahad2ModeInfo
{
    public string Key          { get; init; } = "";
    public string Label        { get; init; } = "";
    public byte   WireByte     { get; init; }
    public bool   HasSpeed     { get; init; }
    public bool   HasDirection { get; init; }
    public bool   HasBrightness{ get; init; }
    public int    ColorsMin    { get; init; }
    public int    ColorsMax    { get; init; }
}

// Mode bytes and color slot counts from OpenRGB LianLiGAIITrinityController.cpp.
public static class Galahad2LightingModes
{
    public static readonly Galahad2ModeInfo[] Catalog =
    {
        new() { Key = "rainbow",              Label = "Rainbow",               WireByte = 0x01, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "rainbowMorph",         Label = "Rainbow Morph",         WireByte = 0x02, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "staticColor",          Label = "Static Color",          WireByte = 0x03, HasSpeed = false, HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "breathingColor",       Label = "Breathing Color",       WireByte = 0x04, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "runway",               Label = "Runway",                WireByte = 0x05, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "meteor",               Label = "Meteor",                WireByte = 0x06, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 4 },
        new() { Key = "vortex",               Label = "Vortex",                WireByte = 0x07, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 4 },
        new() { Key = "crossingOver",         Label = "Crossing Over",         WireByte = 0x08, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 4 },
        new() { Key = "taiChi",               Label = "Tai Chi",               WireByte = 0x09, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "colorfulStarryNight",  Label = "Colorful Starry Night", WireByte = 0x0A, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 0 },
        new() { Key = "staticStarryNight",    Label = "Static Starry Night",   WireByte = 0x0B, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 1 },
        new() { Key = "voice",                Label = "Voice",                 WireByte = 0x0C, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "bigBang",              Label = "Big Bang",              WireByte = 0x0D, HasSpeed = true,  HasDirection = false, HasBrightness = true, ColorsMin = 0, ColorsMax = 4 },
        new() { Key = "pump",                 Label = "Pump",                  WireByte = 0x0E, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 2 },
        new() { Key = "colorsMorph",          Label = "Colors Morph",          WireByte = 0x0F, HasSpeed = true,  HasDirection = true,  HasBrightness = true, ColorsMin = 0, ColorsMax = 0 },
    };

    public static Galahad2ModeInfo? Find(string key)
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
