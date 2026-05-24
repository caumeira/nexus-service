using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public sealed class StubVolumeProvider : IVolumeProvider
{
    public VolumeState GetState() => new() { Supported = false, Volume = 0, Muted = false };
    public void SetVolume(double volume) { }
    public void SetMuted(bool muted) { }
}
