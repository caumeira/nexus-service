using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Np50;

namespace Nexus.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Linux MiniHub discovery: finds <c>/dev/ttyACM*</c> ports whose USB parent is
/// VID 0x3402 / PID 0x0900. Returns <see cref="Np50PortInfo"/> like the rest of
/// the MiniHub stack (it reuses the NP50 transport).
/// </summary>
public sealed class LinuxMiniHubPortDiscovery : INp50PortDiscovery
{
    public IReadOnlyList<Np50PortInfo> Discover() =>
        LinuxSerialDiscovery.Find(MiniHubProtocol.VendorId, MiniHubProtocol.ProductId)
            .Select(m => new Np50PortInfo { PortName = m.PortName, Serial = m.Serial })
            .ToList();
}
