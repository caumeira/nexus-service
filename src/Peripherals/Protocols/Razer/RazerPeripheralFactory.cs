using System.Linq;
using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Peripherals.Protocols.Razer;

/// <summary>
/// Factory for Razer peripherals. Looks up the VID/PID in the profile tables and
/// spawns a generic <see cref="RazerMousePeripheral"/> with the matching profile.
/// New devices go in <see cref="RazerMouseProfiles"/>; no per-device C# files.
/// </summary>
public sealed class RazerPeripheralFactory
{
    public const int RazerVendorId = 0x1532;

    public static bool Supports(int pid) => RazerMouseProfiles.ByPid.ContainsKey(pid);

    public IPeripheral? TryCreate(IHidEnumerator hidEnumerator, int productId, string serial)
    {
        if (RazerMouseProfiles.ByPid.TryGetValue(productId, out var profile))
        {
            var devices = hidEnumerator.Find(RazerVendorId, productId);
            if (devices.Count == 0)
            {
                return null;
            }

            // Razer control interface is the one exposing the 90-byte feature report.
            var info = devices.OrderByDescending(d => d.FeatureReportByteLength).First();
            if (info.FeatureReportByteLength < 91)
            {
                return null;
            }

            var device = hidEnumerator.Open(info.Path);
            if (device is null)
            {
                return null;
            }

            var client = new RazerClient(device);
            return new RazerMousePeripheral(client, profile, RazerVendorId, productId, serial);
        }

        return null;
    }
}
