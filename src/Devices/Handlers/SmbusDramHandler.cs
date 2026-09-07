using System;
using System.Collections.Generic;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// The DIMMs on the chipset SMBus, listed as one device so the user can tell
/// Nexus to leave them alone. Nothing here is a USB handle: the bus is
/// multi-master with only the Global\Access_SMBUS mutex convention between
/// Nexus, its OpenRGB subprocess and vendor apps (iCUE, Armoury Crate), and
/// DIMM detection is a one-shot address probe, so the unit of Nexus Control
/// is the whole bus, never a single stick. Off means every in-process SMBus
/// consumer yields: LibreHardwareMonitor's SPD path (LhmComputer) and the
/// OpenRGB DRAM detectors (OpenRgbProcessManager). Present on Windows only,
/// where the SPD path exists.
/// </summary>
public sealed class SmbusDramHandler : IDeviceHandler
{
    public const string HandlerId = "smbus-dram";

    public string Id => HandlerId;
    public string Name => "Memory";
    public string Category => "memory";
    public IReadOnlyList<UsbId> Identifiers => Array.Empty<UsbId>();
    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices) => OperatingSystem.IsWindows();
    public bool HasPage => false;
    public string GetFirmwareVersion() => "";
}
