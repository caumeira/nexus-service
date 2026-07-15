using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// iBUYPOWER AW5 AIO cooler. The cooler is driven by the vendor's own driver .exe,
/// which the service fetches and launches from the com.ibuypower.control app's
/// driver manifest block (see DriverAutoLaunchWorker). Nexus never opens the device,
/// so presence comes from USB enumeration alone; the Nexus Control gate starts and
/// stops the vendor process rather than a port claim.
/// </summary>
public sealed class Aw5Handler : IDeviceHandler
{
    private const int IbpVid = 0x3402;

    /// <summary>Shared with the panel blanker. The app manifest's deviceId must match it by hand - that one lives in nexus-apps.</summary>
    public const string HandlerId = "aw5";

    public string Id => HandlerId;
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

    /// <summary>
    /// Nexus never claims the device, but the gate still governs the vendor driver
    /// process: off stops the .exe, on lets it run. DriverAutoLaunchWorker honors it
    /// via the app's <c>deviceId</c>.
    /// </summary>
    public bool SupportsNexusControl => true;

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
        => detectedDevices.Any(d => Identifiers.Any(id => id.VendorId == d.VendorId && id.ProductId == d.ProductId));

    /// <summary>Empty: reading it would mean opening a device the vendor driver holds.</summary>
    public string GetFirmwareVersion() => "";
}
