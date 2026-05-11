using System.Collections.Generic;

namespace Qos.Service.Devices;

/// <summary>
/// Defines a modular device handler. Each supported device type implements this interface.
/// Handlers are self-contained — removing a handler file and its DI registration
/// has zero impact on the rest of the application.
/// </summary>
public interface IDeviceHandler
{
    /// <summary>Unique identifier (e.g., "cnvs", "q60").</summary>
    string Id { get; }

    /// <summary>Display name (e.g., "CNVS", "Q60").</summary>
    string Name { get; }

    /// <summary>Device category for grouping (e.g., "controller", "display", "hub", "keyboard").</summary>
    string Category { get; }

    /// <summary>USB VID/PID pairs this handler recognizes.</summary>
    IReadOnlyList<UsbId> Identifiers { get; }

    /// <summary>Returns true if any of the enumerated USB devices match this handler's identifiers.</summary>
    bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices);

    /// <summary>Current firmware version, or empty string if unavailable.</summary>
    string GetFirmwareVersion();
}

/// <summary>USB Vendor ID + Product ID pair.</summary>
public readonly struct UsbId
{
    public int VendorId { get; }
    public int ProductId { get; }

    public UsbId(int vendorId, int productId)
    {
        VendorId = vendorId;
        ProductId = productId;
    }
}

/// <summary>A USB device detected on the system.</summary>
public sealed class UsbDeviceEntry
{
    public int VendorId { get; init; }
    public int ProductId { get; init; }
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Location { get; init; } = "";
    public string Class { get; init; } = "";
    public string Speed { get; init; } = "";
    public string Driver { get; init; } = "";
    public string HardwareId { get; init; } = "";
}
