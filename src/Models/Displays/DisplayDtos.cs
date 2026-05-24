using System.Collections.Generic;

namespace Nexus.Service.Models.Displays;

public sealed class DisplayDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public bool IsInternal { get; set; }
    public bool IsDdcCapable { get; set; }
    public DisplayCapabilitiesDto Capabilities { get; set; } = new();
    public DisplayBrightnessControlDto BrightnessControl { get; set; } = new();
}

public sealed class DisplayCapabilitiesDto
{
    public bool Brightness { get; set; }
    public bool Contrast { get; set; }
    public List<string> ColorTempPresets { get; set; } = new();
    public List<string> InputSources { get; set; } = new();
    public bool Volume { get; set; }
}

public sealed class DisplayBrightnessDto
{
    public string Id { get; set; } = "";
    public int Brightness { get; set; }
    public int RequestedBrightness { get; set; }
    public int AppliedBrightness { get; set; }
    public string Status { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class DisplayBrightnessParams
{
    public int Brightness { get; set; }
}

public sealed class DisplayVcpDto
{
    public string Id { get; set; } = "";
    public byte Code { get; set; }
    public int Value { get; set; }
    public int MaxValue { get; set; }
}

public sealed class DisplayVcpParams
{
    public int Value { get; set; }
}

public sealed class DisplayListResponse
{
    public List<DisplayDto> Displays { get; set; } = new();
    public string Hint { get; set; } = "";
}

public sealed class DisplayBrightnessControlDto
{
    public bool Supported { get; set; }
    public int Min { get; set; } = 0;
    public int Max { get; set; } = 100;
    public int? Current { get; set; }
    public string ControlPath { get; set; } = DisplayBrightnessControlPaths.Unsupported;
    public string WriteMode { get; set; } = DisplayBrightnessWriteModes.Unsupported;
    public int WriteCooldownMs { get; set; }
    public bool VerifyAfterWrite { get; set; }
    public string UnsupportedReason { get; set; } = "";
}

public sealed class DisplayBrightnessWritePolicy
{
    public string ControlPath { get; set; } = DisplayBrightnessControlPaths.Unsupported;
    public string WriteMode { get; set; } = DisplayBrightnessWriteModes.Unsupported;
    public int MinWriteIntervalMs { get; set; }
    public int ReadAfterWriteDelayMs { get; set; }
    public bool VerifyAfterWrite { get; set; }
}

public static class DisplayBrightnessControlPaths
{
    public const string Unsupported = "unsupported";
    public const string DdcCi = "ddc-ci";
    public const string WindowsInternal = "windows-internal";
    public const string LinuxBacklight = "linux-backlight";
    public const string MacosInternal = "macos-internal";
}

public static class DisplayBrightnessWriteModes
{
    public const string Unsupported = "unsupported";
    public const string Immediate = "immediate";
    public const string Coalesced = "coalesced";
    public const string CommitOnly = "commit-only";
}

public static class DisplayBrightnessWriteStatuses
{
    public const string Applied = "applied";
    public const string Unsupported = "unsupported";
    public const string Failed = "failed";
}
