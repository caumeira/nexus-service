using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.Capabilities;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Protocols.Razer;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Mock HID device that records SetFeature calls and serves canned replies.</summary>
internal sealed class MockHidDevice : IHidDevice
{
    public List<byte[]> Sent { get; } = new();
    public Func<byte[], byte[]>? ReplyBuilder { get; set; }
    public int VendorId { get; set; } = 0x1532;
    public int ProductId { get; set; } = 0x007D;
    public string Path { get; set; } = "mock";
    public string? Serial { get; set; } = "MOCK123";
    public int UsagePage { get; set; } = 0x0001;
    public int Usage { get; set; } = 0x0002;

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        Sent.Add(report.ToArray());
        return true;
    }

    public bool GetFeature(Span<byte> buffer)
    {
        var last = Sent.Count > 0 ? Sent[^1] : new byte[91];
        var reply = ReplyBuilder?.Invoke(last) ?? new byte[buffer.Length];
        reply.AsSpan(0, Math.Min(reply.Length, buffer.Length)).CopyTo(buffer);
        return true;
    }

    public bool Write(ReadOnlySpan<byte> report) => true;
    public int Read(Span<byte> buffer, int timeoutMs) => 0;
    public void Dispose() { }
}

public class RazerMousePeripheralTests
{
    private static (MockHidDevice dev, RazerMousePeripheral mouse) Build(int pid = 0x007D)
    {
        var profile = RazerMouseProfiles.ByPid[pid];
        var dev = new MockHidDevice { ProductId = pid };
        var client = new RazerClient(dev);
        var mouse = new RazerMousePeripheral(client, profile, 0x1532, pid, "MOCK");
        return (dev, mouse);
    }

    [Fact]
    public void SetDpi_SendsCorrectBytesWithProfileTxid()
    {
        var (dev, mouse) = Build(0x007D); // V2 Pro Wireless, txid=0x3F
        var dpi = mouse as IDpiCapability;
        Assert.NotNull(dpi);
        Assert.True(dpi!.SetDpi(1600));

        var frame = dev.Sent[0];
        Assert.Equal(91, frame.Length);
        Assert.Equal(0x3F, frame[2]);   // transaction id from profile
        Assert.Equal(0x07, frame[6]);   // data size
        Assert.Equal(0x04, frame[7]);   // command class
        Assert.Equal(0x05, frame[8]);   // command id
        Assert.Equal(0x01, frame[9]);   // VARSTORE
        // DPI 1600 = 0x0640
        Assert.Equal(0x06, frame[10]);
        Assert.Equal(0x40, frame[11]);
        Assert.Equal(0x06, frame[12]);
        Assert.Equal(0x40, frame[13]);
    }

    [Fact]
    public void SetDpi_ClampsToMaxDpi()
    {
        var (dev, mouse) = Build(0x007D);
        ((IDpiCapability)mouse).SetDpi(50000);
        var frame = dev.Sent[0];
        // V2 Pro maxDpi = 20000 = 0x4E20
        Assert.Equal(0x4E, frame[10]);
        Assert.Equal(0x20, frame[11]);
    }

    [Fact]
    public void SetDpi_ClampsToMinDpi()
    {
        var (dev, mouse) = Build(0x007D);
        ((IDpiCapability)mouse).SetDpi(10);
        var frame = dev.Sent[0];
        // min DPI = 100 = 0x0064
        Assert.Equal(0x00, frame[10]);
        Assert.Equal(0x64, frame[11]);
    }

    [Fact]
    public void GetDpi_ParsesReplyArgs()
    {
        var (dev, mouse) = Build(0x007D);
        dev.ReplyBuilder = _ =>
        {
            var r = new byte[91];
            r[1] = 0x02; // status success
            r[2] = 0x3F;
            // args start at r[9]. In the reply, args[1..2] = DPI X big-endian
            r[9] = 0x01;   // VARSTORE echo
            r[10] = 0x06;  // DPI X hi (1600)
            r[11] = 0x40;  // DPI X lo
            return r;
        };
        var dpi = ((IDpiCapability)mouse).GetCurrent();
        Assert.Equal(0x0640, dpi);
    }

    [Fact]
    public void StandardPolling_Set1000UsesCode01()
    {
        var (dev, mouse) = Build(0x007D);
        var poll = (IPollingRateCapability)mouse;
        Assert.Contains(1000, poll.SupportedHz);
        Assert.Contains(500, poll.SupportedHz);
        Assert.Contains(125, poll.SupportedHz);
        Assert.DoesNotContain(8000, poll.SupportedHz);

        poll.SetHz(1000);
        var frame = dev.Sent[0];
        Assert.Equal(0x00, frame[7]);  // class
        Assert.Equal(0x05, frame[8]);  // id
        Assert.Equal(0x01, frame[9]);  // code for 1000 Hz
    }

    [Fact]
    public void HyperPolling_Set8000UsesSetPollingRate2Command()
    {
        // DeathAdder V3 Pro - HyperPolling variant
        var (dev, mouse) = Build(0x00B7);
        var poll = (IPollingRateCapability)mouse;
        Assert.Contains(8000, poll.SupportedHz);
        Assert.Contains(2000, poll.SupportedHz);
        Assert.Contains(4000, poll.SupportedHz);

        poll.SetHz(8000);
        var frame = dev.Sent[0];
        // set_polling_rate2: class 0x00, id 0x40, data_size 0x02, args[0]=varstore, args[1]=code
        Assert.Equal(0x00, frame[7]);
        Assert.Equal(0x40, frame[8]);
        Assert.Equal(0x02, frame[6]);
        Assert.Equal(0x01, frame[9]);  // VARSTORE
        Assert.Equal(0x01, frame[10]); // 8000 Hz code
    }

    [Fact]
    public void Battery_OnlyPresentForWirelessProfiles()
    {
        var (_, wired) = Build(0x007C);      // V2 Pro Wired - no battery
        var (_, wireless) = Build(0x007D);   // V2 Pro Wireless - battery

        Assert.Null(wired.GetCapability<IBatteryCapability>());
        Assert.NotNull(wireless.GetCapability<IBatteryCapability>());
    }

    [Fact]
    public void Battery_Scales0To255InputTo0To100Percent()
    {
        var (dev, mouse) = Build(0x007D);
        dev.ReplyBuilder = _ =>
        {
            var r = new byte[91];
            r[1] = 0x02;
            r[10] = 0xFF;   // args[1] = 255 = 100%
            return r;
        };
        var bat = mouse.GetCapability<IBatteryCapability>();
        Assert.NotNull(bat);
        Assert.Equal(100, bat!.GetPercent());
    }

    [Fact]
    public void Capabilities_IncludeBatteryAndSleepForWireless()
    {
        var (_, mouse) = Build(0x007D);
        Assert.Contains("dpi", mouse.Capabilities);
        Assert.Contains("polling", mouse.Capabilities);
        Assert.Contains("battery", mouse.Capabilities);
        Assert.Contains("sleep", mouse.Capabilities);
    }

    [Fact]
    public void Capabilities_ExcludeBatteryForWired()
    {
        var (_, mouse) = Build(0x007C);  // wired
        Assert.Contains("dpi", mouse.Capabilities);
        Assert.DoesNotContain("battery", mouse.Capabilities);
        Assert.DoesNotContain("sleep", mouse.Capabilities);
    }
}
