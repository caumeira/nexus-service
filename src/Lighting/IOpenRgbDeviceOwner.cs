using Nexus.Service.Lighting.Rgb;

namespace Nexus.Service.Lighting;

/// <summary>
/// Implemented by first-party lighting providers that also claim an OpenRGB
/// device, so <see cref="Rgb.RgbBridge"/> can exclude those devices from
/// engine frame seeding.
///
/// Without this exclusion the OpenRGB representation of a first-party device
/// survives into the engine and <see cref="Rgb.RgbBridge"/> pushes rendered
/// colors to it via the TCP controller, conflicting with the first-party
/// writer. When both paths reach the same physical hardware (e.g. Lian Li
/// via USB HID vs CDC-serial) the first-party disable/brightness control has
/// no effect because OpenRGB overwrites black frames with effect colors.
/// </summary>
public interface IOpenRgbDeviceOwner
{
    /// <summary>
    /// Returns true when this provider owns <paramref name="device"/> and
    /// OpenRGB must not drive it.
    /// </summary>
    bool OwnsOpenRgbDevice(RgbDevice device);
}
