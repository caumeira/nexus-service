using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Linux Smart Hub discovery: finds <c>/dev/ttyACM*</c> ports whose USB parent is
/// VID 0x3402 / PID 0x0904. Returns <see cref="Np50PortInfo"/> like the rest of
/// the Smart Hub stack (it reuses the NP50 transport).
/// </summary>
public sealed class LinuxSmartHubPortDiscovery : INp50PortDiscovery
{
    public IReadOnlyList<Np50PortInfo> Discover() =>
        LinuxSerialDiscovery.Find(SmartHubProtocol.VendorId, SmartHubProtocol.ProductId)
            .Select(m => new Np50PortInfo { PortName = m.PortName, Serial = m.Serial })
            .ToList();
}
