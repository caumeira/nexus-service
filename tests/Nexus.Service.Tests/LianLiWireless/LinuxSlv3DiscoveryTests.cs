using System.IO;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public class LinuxSlv3DiscoveryTests
{
    private static void WriteDevice(string root, string name, string vid, string pid, int bus, int dev, string? serial = null)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "idVendor"), vid + "\n");
        File.WriteAllText(Path.Combine(dir, "idProduct"), pid + "\n");
        File.WriteAllText(Path.Combine(dir, "busnum"), bus + "\n");
        File.WriteAllText(Path.Combine(dir, "devnum"), dev + "\n");
        if (serial is not null)
        {
            File.WriteAllText(Path.Combine(dir, "serial"), serial + "\n");
        }
    }

    [NonWindowsFact]
    public void Maps_each_dongle_to_its_usbfs_node_and_role()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nexus-slv3-test-{Path.GetRandomFileName()}");
        try
        {
            // The Y70's module: RX on 1-10.2 (dev 6), TX on 1-10.3 (dev 8), no serials.
            WriteDevice(root, "1-10.2", "0416", "8041", 1, 6);
            WriteDevice(root, "1-10.3", "0416", "8040", 1, 8);
            // WCH alias of the same controller, with a serial.
            WriteDevice(root, "3-1", "1a86", "e304", 3, 112, serial: "ABC123");
            // Not a dongle: the SL-Infinity wired hub.
            WriteDevice(root, "1-7", "0cf2", "a102", 1, 19);
            // Interface nodes and root hubs carry no dongle identity.
            WriteDevice(root, "1-10.3:1.0", "0416", "8040", 1, 8);
            WriteDevice(root, "usb1", "0416", "8040", 1, 1);

            var ports = LinuxSlv3Discovery.DiscoverFrom(root).OrderBy(p => p.PortName).ToList();

            Assert.Equal(3, ports.Count);
            Assert.Equal("/dev/bus/usb/001/006", ports[0].PortName);
            Assert.Equal(Slv3DongleRole.Rx, ports[0].Role);
            Assert.Equal("1-10.2", ports[0].Serial);
            Assert.Equal("/dev/bus/usb/001/008", ports[1].PortName);
            Assert.Equal(Slv3DongleRole.Tx, ports[1].Role);
            Assert.Equal("/dev/bus/usb/003/112", ports[2].PortName);
            Assert.Equal(Slv3DongleRole.Tx, ports[2].Role);
            Assert.Equal("ABC123", ports[2].Serial);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Missing_sysfs_root_finds_nothing()
    {
        Assert.Empty(LinuxSlv3Discovery.DiscoverFrom("/tmp/definitely-not-a-sysfs-dir-slv3"));
    }
}
