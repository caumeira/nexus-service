using System;
using System.Collections.Generic;

namespace Nexus.Service.Devices.Handlers;

/// <summary>
/// The DIMMs on the chipset SMBus as one device. The bus is multi-master with
/// only the Global\Access_SMBUS mutex between Nexus, its OpenRGB subprocess and
/// vendor apps, and DIMM detection is a one-shot address probe, so the unit of
/// Nexus Control is the whole bus, never a stick. Off means every in-process
/// consumer yields: LHM's SPD path and the OpenRGB DRAM detectors. Windows
/// only, where that SPD path exists.
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
