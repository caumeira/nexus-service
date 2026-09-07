using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Plugins;
using Xunit;

namespace Nexus.Service.Tests.Devices;

/// <summary>
/// The chipset SMBus as a device: one row, bus "smbus", whose Nexus Control
/// default yields to an installed vendor app and which never auto-adopts.
/// </summary>
public class SmbusDramDeviceTests
{
    private sealed class FixedInstallProbe : IConflictAppInstallProbe
    {
        private readonly HashSet<string> _installed;
        public FixedInstallProbe(params string[] installed) => _installed = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        public bool IsInstalled(string appId) => _installed.Contains(appId);
        public IReadOnlyList<string> InstalledAppIds() => _installed.ToList();
    }

    [Fact]
    public void Handler_HasNoUsbIdentity_AndNoPage()
    {
        IDeviceHandler h = new SmbusDramHandler();
        Assert.Equal("smbus-dram", h.Id);
        Assert.Empty(h.Identifiers);
        Assert.True(h.SupportsNexusControl);
        Assert.False(h.HasPage);
        Assert.Equal("", h.GetFirmwareVersion());
        Assert.Equal(OperatingSystem.IsWindows(), h.IsConnected(Array.Empty<UsbDeviceEntry>()));
    }

    [Fact]
    public void GetAll_CarriesTheBusAndTheGateOntoTheWire()
    {
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), new SmbusDramHandler() },
            new FixedUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(new InMemoryConfigStore()));

        var items = manager.GetAll();

        var memory = items.Single(i => i.Id == SmbusDramHandler.HandlerId);
        Assert.Equal("smbus", memory.Bus);
        Assert.Equal("memory", memory.Category);
        Assert.True(memory.SupportsNexusControl);
        Assert.False(memory.Experimental);
        Assert.False(memory.HasPage);
        Assert.Equal("icue", memory.ConflictAppId);
        Assert.Equal("usb", items.Single(i => i.Id == "cnvs").Bus);
    }

    [Fact]
    public void Policy_BusFor_IsUsbForEverythingButTheSmbusDevice()
    {
        Assert.Equal("smbus", DeviceControlPolicy.BusFor(SmbusDramHandler.HandlerId));
        Assert.Equal("usb", DeviceControlPolicy.BusFor("cnvs"));
        Assert.Equal("usb", DeviceControlPolicy.BusFor("corsair"));
    }

    [Fact]
    public void Policy_DefaultOn_YieldsOnlyWhereTheAppIsInstalled()
    {
        Assert.True(DeviceControlPolicy.DefaultOn(SmbusDramHandler.HandlerId));
        Assert.True(DeviceControlPolicy.DefaultOn(SmbusDramHandler.HandlerId, _ => false));
        Assert.False(DeviceControlPolicy.DefaultOn(SmbusDramHandler.HandlerId, app => app == "icue"));
        // A USB hub mapped to a competing app keeps its unconditional default.
        Assert.False(DeviceControlPolicy.DefaultOn("corsair", _ => false));
        Assert.True(DeviceControlPolicy.DefaultOn("cnvs", _ => true));
    }

    [Fact]
    public void Policy_ConflictAppShowsInTheUi_ButNeverDrivesAdoption()
    {
        Assert.Equal("icue", DeviceControlPolicy.ConflictAppFor(SmbusDramHandler.HandlerId));
        Assert.Null(DeviceControlPolicy.AdoptionConflictAppFor(SmbusDramHandler.HandlerId));
        Assert.Equal("icue", DeviceControlPolicy.AdoptionConflictAppFor("corsair"));
        Assert.False(DeviceControlPolicy.IsExperimental(SmbusDramHandler.HandlerId));
    }

    [Fact]
    public void Gate_DefaultsOff_WhenTheProbeSaysICueIsInstalled()
    {
        var installed = new DeviceControlGate(new InMemoryConfigStore(), new FixedInstallProbe("icue"));
        var clean = new DeviceControlGate(new InMemoryConfigStore(), new FixedInstallProbe());
        var unprobed = new DeviceControlGate(new InMemoryConfigStore());

        Assert.False(installed.IsEnabled(SmbusDramHandler.HandlerId));
        Assert.True(clean.IsEnabled(SmbusDramHandler.HandlerId));
        Assert.True(unprobed.IsEnabled(SmbusDramHandler.HandlerId));
        // The probe never touches a USB handler's default.
        Assert.True(installed.IsEnabled("cnvs"));
    }

    [Fact]
    public void Gate_ExplicitChoice_BeatsTheInstalledDefault_AndRaisesChanged()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore(), new FixedInstallProbe("icue"));
        var raised = new List<(string Id, bool Enabled)>();
        gate.Changed += (id, enabled) => raised.Add((id, enabled));

        gate.SetEnabled(SmbusDramHandler.HandlerId, true);
        Assert.True(gate.IsEnabled(SmbusDramHandler.HandlerId));

        gate.SetEnabled(SmbusDramHandler.HandlerId, false);
        Assert.False(gate.IsEnabled(SmbusDramHandler.HandlerId));

        Assert.Equal(new[] { (SmbusDramHandler.HandlerId, true), (SmbusDramHandler.HandlerId, false) }, raised);
    }

    [Fact]
    public void InstallProbe_UnknownApp_IsNotInstalled()
    {
        var probe = new ConflictAppInstallProbe();
        Assert.False(probe.IsInstalled("no-such-app"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(probe.IsInstalled("icue"));
        }
    }

    [Fact]
    public void InstallProbe_MatchesAServiceByKeyOrDisplayName()
    {
        // The catalog names iCUE's control service the way the executable is
        // named; Windows registers it under that key and displays it spaced.
        var wanted = new[] { "CorsairDeviceListerService", "CorsairDeviceControlService", "iCUE" };

        Assert.True(ConflictAppInstallProbe.Matches(wanted, "CorsairDeviceControlService"));
        Assert.True(ConflictAppInstallProbe.Matches(wanted, "Corsair Device Control Service"));
        Assert.True(ConflictAppInstallProbe.Matches(wanted, "corsair-device_control.service"));
        Assert.True(ConflictAppInstallProbe.Matches(wanted, "iCue"));

        Assert.False(ConflictAppInstallProbe.Matches(wanted, "CorsairDeviceControl"));
        Assert.False(ConflictAppInstallProbe.Matches(wanted, "NvContainerLocalSystem"));
        Assert.False(ConflictAppInstallProbe.Matches(wanted, ""));
        Assert.False(ConflictAppInstallProbe.Matches(wanted, "   "));
    }
}
