using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Linux NP50 discovery: finds <c>/dev/ttyACM*</c> ports whose USB parent is
/// VID 0x3402 / PID 0x0901. Counterpart to <see cref="WindowsNp50PortDiscovery"/>.
/// </summary>
public sealed class LinuxNp50PortDiscovery : INp50PortDiscovery
{
    public IReadOnlyList<Np50PortInfo> Discover() =>
        LinuxSerialDiscovery.Find(Np50Protocol.VendorId, Np50Protocol.ProductId)
            .Select(m => new Np50PortInfo { PortName = m.PortName, Serial = m.Serial })
            .ToList();
}
