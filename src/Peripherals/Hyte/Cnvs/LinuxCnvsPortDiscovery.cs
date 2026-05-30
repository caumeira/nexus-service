using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Linux CNVS discovery: finds <c>/dev/ttyACM*</c> ports whose USB parent is
/// VID 0x3402 / one of <see cref="CnvsProtocol.ProductIds"/>. Surfaces the
/// matched PID so the hub can pick the firmware variant. Counterpart to
/// <see cref="WindowsCnvsPortDiscovery"/>.
/// </summary>
public sealed class LinuxCnvsPortDiscovery : ICnvsPortDiscovery
{
    public IReadOnlyList<CnvsPortInfo> Discover() =>
        LinuxSerialDiscovery.Find(CnvsProtocol.VendorId, CnvsProtocol.ProductIds)
            .Select(m => new CnvsPortInfo { PortName = m.PortName, Serial = m.Serial, ProductId = m.ProductId })
            .ToList();
}
