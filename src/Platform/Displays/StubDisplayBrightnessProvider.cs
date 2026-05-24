using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>No-op provider for platforms without a real implementation yet.</summary>
public sealed class StubDisplayBrightnessProvider : IDisplayBrightnessProvider
{
    public string Hint => "";
    public IReadOnlyList<DisplayDto> Enumerate() => System.Array.Empty<DisplayDto>();
    public int? GetBrightness(string id) => null;
    public DisplayBrightnessDto SetBrightness(string id, int percent) => new()
    {
        Id = id,
        RequestedBrightness = ClampPercent(percent),
        AppliedBrightness = 0,
        Brightness = 0,
        Status = DisplayBrightnessWriteStatuses.Unsupported,
        Error = "Display brightness is not supported on this platform yet.",
    };
    public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new();
    public DisplayVcpDto? GetVcp(string id, byte code) => null;
    public bool SetVcp(string id, byte code, int value) => false;

    private static int ClampPercent(int value) => value < 0 ? 0 : value > 100 ? 100 : value;
}
