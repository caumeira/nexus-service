using Nexus.Service.Models.Devices;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Shared one-at-a-time gate for all flash operations.
/// Both FirmwareFlasher (cooler DFU) and ApkFlasher (panel APK) acquire this
/// before starting, so the two are mutually exclusive and all restart/OTA guards
/// that read IsFlashing cover both without per-call-site changes.
/// </summary>
public sealed class FlashGate
{
    private readonly object _lock = new();
    private volatile bool _active;

    public FlashStatusDto Status { get; } = new();

    /// <summary>True while any flash (DFU or APK) is running.</summary>
    public bool IsFlashing => _active;

    /// <summary>
    /// Try to acquire the gate. Returns true and sets Active on the Status;
    /// returns false with a reason when already held.
    /// </summary>
    public bool TryAcquire(out string error)
    {
        lock (_lock)
        {
            if (_active)
            {
                error = "A firmware update is already in progress.";
                return false;
            }
            _active = true;
            Status.Active = true;
            error = "";
            return true;
        }
    }

    /// <summary>Release the gate; called from each flasher's finally block.</summary>
    public void Release()
    {
        _active = false;
        Status.Active = false;
    }
}
