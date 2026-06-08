using System.Collections.Generic;
using Nexus.Service.Models;

namespace Nexus.Service.Models.Activity;

public sealed class AudioDevice
{
    /// <summary>Stable per-OS id (Windows endpoint id, macOS device UID, Linux sink/source name).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>"output" or "input".</summary>
    public string Direction { get; set; } = "output";
}

public sealed class AudioDeviceList : ApiResponse
{
    public List<AudioDevice> Outputs { get; set; } = new();
    public List<AudioDevice> Inputs { get; set; } = new();
}

public sealed class SetAudioDefaultBody
{
    public string DeviceId { get; set; } = "";
}
