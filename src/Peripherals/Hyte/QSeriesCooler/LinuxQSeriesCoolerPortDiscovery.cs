using System.Collections.Generic;
using System.Linq;

namespace Nexus.Service.Peripherals.Hyte.QSeriesCooler;

/// <summary>
/// Linux Q-series cooler discovery: finds <c>/dev/ttyACM*</c> ports whose USB
/// parent is VID 0x3402 / PID 0x0400 (Q60) or 0x0403 (Q80), tagging each with
/// its variant. Counterpart to <see cref="WindowsQSeriesCoolerPortDiscovery"/>.
/// </summary>
public sealed class LinuxQSeriesCoolerPortDiscovery : IQSeriesCoolerPortDiscovery
{
    public IReadOnlyList<QSeriesCoolerPort> Discover() =>
        LinuxSerialDiscovery.Find(
                QSeriesCoolerProtocol.VendorId,
                QSeriesCoolerProtocol.Q60ProductId,
                QSeriesCoolerProtocol.Q80ProductId)
            .Select(m => new QSeriesCoolerPort
            {
                PortName = m.PortName,
                Serial = m.Serial,
                Variant = m.ProductId == QSeriesCoolerProtocol.Q80ProductId
                    ? QSeriesCoolerProtocol.VariantQ80
                    : QSeriesCoolerProtocol.VariantQ60,
            })
            .ToList();
}
