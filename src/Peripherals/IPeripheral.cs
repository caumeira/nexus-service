using System.Collections.Generic;

namespace Nexus.Service.Peripherals;

/// <summary>
/// Common interface for third-party peripherals (Logitech mice, Razer mice/keyboards,
/// Corsair, etc.) that Nexus can read and configure beyond simple VID/PID detection.
///
/// Capabilities are composable traits — each peripheral implements whichever subset
/// of capability interfaces its protocol supports. Consumers check via
/// <see cref="HasCapability{T}"/> / <see cref="GetCapability{T}"/>.
/// </summary>
public interface IPeripheral
{
    /// <summary>Stable identifier. Usually "vendor-model-serial" form.</summary>
    string Id { get; }

    /// <summary>Human-readable name (e.g. "Razer DeathAdder V2 Pro").</summary>
    string Name { get; }

    /// <summary>Vendor name (e.g. "Razer", "Logitech", "Corsair").</summary>
    string Vendor { get; }

    /// <summary>Category: "mouse", "keyboard", "headset", "controller", "trackball".</summary>
    string Category { get; }

    int VendorId { get; }
    int ProductId { get; }
    string Serial { get; }

    /// <summary>Firmware version string, or empty if unavailable.</summary>
    string FirmwareVersion { get; }

    /// <summary>True if connected via wireless (dongle / Bluetooth).</summary>
    bool IsWireless { get; }

    /// <summary>Advertised capability identifiers (keys: "dpi", "polling", "battery", ...).</summary>
    IReadOnlyList<string> Capabilities { get; }

    /// <summary>
    /// Returns a capability implementation if supported, else null.
    /// </summary>
    T? GetCapability<T>() where T : class, IPeripheralCapability;
}

/// <summary>Marker interface for capability traits. Actual capability keys are surfaced via IPeripheral.Capabilities.</summary>
public interface IPeripheralCapability
{
}
