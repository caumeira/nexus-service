namespace Qos.Service.Peripherals.Capabilities;

/// <summary>
/// Generic on/off toggle capability used for FN lock, game mode, smooth scroll, etc.
/// The concrete capability key ("fnLock", "gameMode", "smoothScroll") is returned via <see cref="Key"/>.
/// </summary>
public interface IToggleCapability : IPeripheralCapability
{
    /// <summary>Capability key (e.g. "fnLock", "gameMode"). Used to render and route API calls.</summary>
    string ToggleKey { get; }
    string Label { get; }
    bool GetEnabled();
    bool SetEnabled(bool enabled);
}
