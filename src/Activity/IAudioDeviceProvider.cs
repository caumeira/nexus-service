using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Enumerates audio output/input devices and switches the system default.
/// Used by the deck "switch audio device" action. Separate from
/// <see cref="IVolumeProvider"/> (which only touches the current default
/// endpoint's level/mute).
/// </summary>
public interface IAudioDeviceProvider
{
    AudioDeviceList ListDevices();
    bool SetDefaultOutput(string deviceId);
    bool SetDefaultInput(string deviceId);
}
