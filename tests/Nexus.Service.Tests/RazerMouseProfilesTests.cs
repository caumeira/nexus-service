using Nexus.Service.Peripherals.Protocols.Razer;
using Xunit;

namespace Nexus.Service.Tests;

public class RazerMouseProfilesTests
{
    [Fact]
    public void ByPid_LoadsDeathAdderV2ProWirelessWithCorrectTxid()
    {
        var profile = RazerMouseProfiles.ByPid[0x007D];
        Assert.Equal("DeathAdder V2 Pro", profile.Name);
        Assert.Equal(0x3F, profile.TransactionId);
        Assert.Equal(20000, profile.MaxDpi);
        Assert.Equal(RazerPollingVariant.Standard, profile.PollingVariant);
        Assert.True(profile.HasBattery);
        Assert.True(profile.HasSleep);
    }

    [Fact]
    public void ByPid_LoadsDeathAdderV3ProAsHyperPolling()
    {
        var profile = RazerMouseProfiles.ByPid[0x00B7];
        Assert.Equal(0x1F, profile.TransactionId);
        Assert.Equal(RazerPollingVariant.HyperPolling, profile.PollingVariant);
    }

    [Fact]
    public void ByPid_CobraUsesTxidFF()
    {
        var profile = RazerMouseProfiles.ByPid[0x00A3];
        Assert.Equal(0xFF, profile.TransactionId);
    }

    [Fact]
    public void ByPid_ContainsAllMajorRazerFamilies()
    {
        // DeathAdder
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x007C));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x007D));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x00B7));
        // Viper
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x00A6));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x0091));
        // Basilisk
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x0099));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x00AB));
        // Naga
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x0090));
        // Cobra / Orochi / Pro Click / Mamba
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x00A3));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x0094));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x0077));
        Assert.True(RazerMouseProfiles.ByPid.ContainsKey(0x0072));
    }

    [Fact]
    public void ByPid_HasReasonableCount()
    {
        // Sanity floor, not an exact count: confirms the JSON table loaded.
        Assert.True(RazerMouseProfiles.ByPid.Count > 40, $"Expected >40 profiles, got {RazerMouseProfiles.ByPid.Count}");
    }

    [Fact]
    public void ByPid_AllProfilesHaveValidTransactionIdAndMaxDpi()
    {
        foreach (var (pid, profile) in RazerMouseProfiles.ByPid)
        {
            Assert.InRange(profile.TransactionId, 0, 0xFF);
            Assert.True(profile.MaxDpi >= 100, $"PID 0x{pid:X4} has MaxDpi {profile.MaxDpi}");
            Assert.True(profile.MaxDpi <= 100000, $"PID 0x{pid:X4} has MaxDpi {profile.MaxDpi}");
            Assert.False(string.IsNullOrWhiteSpace(profile.Name));
        }
    }
}
