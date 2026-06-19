namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// A connected device whose serial hub can drop it into DFU bootloader mode for
/// flashing. Implemented by the HYTE serial hubs (CNVS, Q-series, Y70, MiniHub,
/// NP50). <see cref="FirmwareFlasher"/> picks the target that matches the
/// requested firmware-catalog key.
/// </summary>
public interface IDfuFlashTarget
{
    bool IsConnected { get; }

    /// <summary>
    /// The connected device's firmware-catalog key (its variant, e.g.
    /// "cnvs-left", "q60", "y70-infinite"), or empty when not connected/known.
    /// This is the key the prod "install latest" path targets.
    /// </summary>
    string FirmwareType { get; }

    /// <summary>
    /// True if this physical device can be flashed with the given catalog key -
    /// i.e. the key belongs to this device's family (CNVS handles all cnvs-*,
    /// Q-series handles q60/q80, Y70 handles y70-*). Lets dev mode flash a
    /// sibling-variant image through the connected device.
    /// </summary>
    bool CanFlash(string firmwareType);

    /// <summary>
    /// Release the COM port and send the in-app DFU-entry handshake. After this
    /// the device reboots into the bootloader and re-enumerates as 3402:0a00.
    /// Returns true if the handshake was sent.
    /// </summary>
    bool EnterDfuMode();
}
