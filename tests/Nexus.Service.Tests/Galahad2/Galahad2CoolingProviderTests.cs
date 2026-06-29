using System.Threading;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Tests.Galahad2;

public class Galahad2CoolingProviderTests
{
    private static byte[] BuildHandshakeReply(int fanRpm = 0, int pumpRpm = 0)
    {
        var packet = new byte[CommandPacket.Length];
        packet[0] = CommandPacket.ReportId;
        packet[1] = 0x81;
        packet[5] = 4;
        int offset = CommandPacket.PayloadOffset;
        packet[offset] = (byte)(fanRpm >> 8);
        packet[offset + 1] = (byte)(fanRpm & 0xFF);
        packet[offset + 2] = (byte)(pumpRpm >> 8);
        packet[offset + 3] = (byte)(pumpRpm & 0xFF);
        return packet;
    }

    private static (Galahad2Hub hub, Galahad2CoolingProvider provider, Galahad2DeviceFake fake) BuildConnected()
    {
        var fake = new Galahad2DeviceFake(BuildHandshakeReply());
        var hub = new Galahad2Hub();
        hub.Attach(fake);
        hub.Connect();
        var provider = new Galahad2CoolingProvider(hub);
        return (hub, provider, fake);
    }

    // ---- Pump duty clamp ----

    [Theory]
    [InlineData(0, 50)]
    [InlineData(25, 50)]
    [InlineData(49, 50)]
    [InlineData(50, 50)]
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    public void SetFanSpeed_pump_channel_clamps_to_pump_floor(int request, int expectedReturn)
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            int result = provider.SetFanSpeed("lianli-aio:pump", request);
            Assert.Equal(expectedReturn, result);
        }
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(25, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void SetFanSpeed_pump_sends_floored_duty_to_hub(int request, int expectedDuty)
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            provider.SetFanSpeed("lianli-aio:pump", request);
            Assert.Equal(expectedDuty, hub.Snapshot.PumpDuty);
        }
    }

    // ---- Fan duty (no floor) ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    public void SetFanSpeed_fan_channel_clamps_to_0_100(int request, int expectedReturn)
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            int result = provider.SetFanSpeed("lianli-aio:fan", request);
            Assert.Equal(expectedReturn, result);
        }
    }

    // ---- GetFanChannels pump metadata ----

    [Fact]
    public void GetFanChannels_pump_channel_has_Kind_Pump_and_MinDuty_50()
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            var channels = provider.GetFanChannels();
            var pump = channels.Single(c => c.Id == "lianli-aio:pump");
            Assert.Equal(FanKinds.Pump, pump.Kind);
            Assert.Equal(50, pump.MinDuty);
        }
    }

    [Fact]
    public void GetFanChannels_fan_channel_has_Kind_Fan()
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            var channels = provider.GetFanChannels();
            var fan = channels.Single(c => c.Id == "lianli-aio:fan");
            Assert.Equal(FanKinds.Fan, fan.Kind);
        }
    }

    [Fact]
    public void GetFanChannels_returns_empty_when_hub_not_connected()
    {
        var hub = new Galahad2Hub();
        var provider = new Galahad2CoolingProvider(hub);

        var channels = provider.GetFanChannels();

        Assert.Empty(channels);
    }

    // ---- CalibrateAsync ----

    [Fact]
    public async Task CalibrateAsync_returns_empty_for_pump_channel()
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            var result = await provider.CalibrateAsync(
                new[] { "lianli-aio:pump" },
                new Progress<FanCalibrationProgress>(),
                CancellationToken.None);
            Assert.Empty(result);
        }
    }

    [Fact]
    public async Task CalibrateAsync_returns_empty_for_all_channels()
    {
        var (hub, provider, _) = BuildConnected();
        using (hub)
        {
            var result = await provider.CalibrateAsync(
                new[] { "lianli-aio:fan", "lianli-aio:pump" },
                new Progress<FanCalibrationProgress>(),
                CancellationToken.None);
            // Neither fan nor pump calibrated; pump must never be driven to 0%.
            Assert.Empty(result);
        }
    }

    [Fact]
    public async Task CalibrateAsync_never_sends_pump_duty_zero()
    {
        var (hub, provider, fake) = BuildConnected();
        using (hub)
        {
            await provider.CalibrateAsync(
                new[] { "lianli-aio:fan", "lianli-aio:pump" },
                new Progress<FanCalibrationProgress>(),
                CancellationToken.None);

            // Find any set-pump command (0x8A) in the writes.
            var pumpWrites = fake.Writes.FindAll(w => w.Length > 1 && w[1] == 0x8A);
            // No pump command should have been sent at all.
            Assert.Empty(pumpWrites);
        }
    }

    // ---- Snapshot lifecycle ----

    [Fact]
    public void Detach_leaves_snapshot_at_empty_with_no_exception()
    {
        var (hub, provider, _) = BuildConnected();
        hub.Detach();

        // Snapshot fields are all 0, IsConnected is false; no exception.
        Assert.False(hub.IsConnected);
        Assert.Equal(0, hub.Snapshot.FanRpm);
        Assert.Equal(0, hub.Snapshot.PumpRpm);
        Assert.Equal(0, hub.Snapshot.FanDuty);
        Assert.Equal(0, hub.Snapshot.PumpDuty);

        // GetFanChannels on a disconnected hub returns empty without throwing.
        var channels = provider.GetFanChannels();
        Assert.Empty(channels);

        hub.Dispose();
    }
}
