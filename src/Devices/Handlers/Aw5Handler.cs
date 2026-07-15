using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// iBUYPOWER AW5 AIO cooler. Recognition only: the cooler is driven by the
/// vendor's own driver .exe, which the service fetches and launches from the
/// com.ibuypower.control app's driver manifest block (see DriverAutoLaunchWorker).
/// Nexus never opens the device, so this handler reports presence from USB
/// enumeration alone and offers no control surface.
/// </summary>
public sealed class Aw5Handler : IDeviceHandler
{
    private const int IbpVid = 0x3402;

    public string Id => "aw5";
    public string Name => "iBUYPOWER AW5";
    public string Category => "cooler";

    /// <summary>
    /// One PID per ODM variant. Apaltek (0x0405) is deliberately absent: no
    /// driver binary is published for it, so recognizing it would list a cooler
    /// Nexus cannot drive.
    /// </summary>
    public IReadOnlyList<UsbId> Identifiers { get; } = new[]
    {
        new UsbId(IbpVid, 0x0406), // Levelplay
        new UsbId(IbpVid, 0x0407), // CoolerMaster
    };

    /// <summary>The vendor driver owns the device; the Nexus Control gate has nothing to gate.</summary>
    public bool SupportsNexusControl => false;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
        => detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    /// <summary>Empty: reading it would mean opening a device the vendor driver holds.</summary>
    public string GetFirmwareVersion() => "";
}
