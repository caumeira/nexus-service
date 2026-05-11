using System;
using Qos.Service.Peripherals.Protocols.Razer;
using Xunit;

namespace Qos.Service.Tests;

public class RazerFramingTests
{
    [Fact]
    public void ToHidFeatureBuffer_ProducesRazerFrameLayout()
    {
        var report = RazerReport.Command(0x04, 0x05, 0x07, new byte[] { 0x01, 0x06, 0x40, 0x06, 0x40, 0, 0 });
        report.TransactionId = 0x3F;

        var buf = report.ToHidFeatureBuffer();

        Assert.Equal(91, buf.Length);
        Assert.Equal(0x00, buf[0]);    // HID report id
        Assert.Equal(0x00, buf[1]);    // status
        Assert.Equal(0x3F, buf[2]);    // transaction id
        Assert.Equal(0x00, buf[3]);    // remaining hi
        Assert.Equal(0x00, buf[4]);    // remaining lo
        Assert.Equal(0x00, buf[5]);    // protocol type
        Assert.Equal(0x07, buf[6]);    // data size
        Assert.Equal(0x04, buf[7]);    // command class
        Assert.Equal(0x05, buf[8]);    // command id
        Assert.Equal(0x01, buf[9]);    // VARSTORE
        Assert.Equal(0x06, buf[10]);   // DPI X hi (1600 = 0x0640)
        Assert.Equal(0x40, buf[11]);   // DPI X lo
        Assert.Equal(0x06, buf[12]);   // DPI Y hi
        Assert.Equal(0x40, buf[13]);   // DPI Y lo
        Assert.Equal(0x00, buf[90]);   // reserved
    }

    [Fact]
    public void ComputeCrc_XorsPayloadBytes3To88Inclusive()
    {
        // Build a buffer where we know the expected CRC by hand.
        // Indices 3..88 of the 91-byte HID feature buffer map to OpenRazer's
        // razer_report bytes 2..87 (after the leading HID report id byte).
        var buf = new byte[91];
        buf[3] = 0x10;
        buf[4] = 0x20;
        buf[5] = 0x30;
        // 0x10 ^ 0x20 ^ 0x30 = 0x00
        Assert.Equal(0x00, RazerReport.ComputeCrc(buf));

        var buf2 = new byte[91];
        buf2[3] = 0xAB;
        buf2[50] = 0xCD;
        // 0xAB ^ 0xCD = 0x66
        Assert.Equal(0x66, RazerReport.ComputeCrc(buf2));

        var buf3 = new byte[91];
        // Bytes outside [3..88] must not affect CRC
        buf3[0] = 0xFF;
        buf3[1] = 0xFF;
        buf3[2] = 0xFF;
        buf3[89] = 0xFF;
        buf3[90] = 0xFF;
        Assert.Equal(0x00, RazerReport.ComputeCrc(buf3));
    }

    [Fact]
    public void ToHidFeatureBuffer_StoresCrcAtByte89()
    {
        var report = RazerReport.Command(0x07, 0x80, 0x02, new byte[] { 0, 0 });
        report.TransactionId = 0x3F;
        var buf = report.ToHidFeatureBuffer();

        // Recompute by hand
        byte expected = 0;
        for (var i = 3; i <= 88; i++)
        {
            expected ^= buf[i];
        }
        Assert.Equal(expected, buf[89]);
    }

    [Fact]
    public void Parse_RoundTripsValues()
    {
        // Fake a device reply
        var buf = new byte[91];
        buf[0] = 0x00;
        buf[1] = 0x02;   // status = success
        buf[2] = 0x3F;   // txid
        buf[6] = 0x02;   // data size
        buf[7] = 0x07;   // class
        buf[8] = 0x80;   // id
        buf[10] = 0xC0;  // args[1] = battery raw 192 (~75%)

        var parsed = RazerReport.Parse(buf);
        Assert.NotNull(parsed);
        Assert.Equal(0x02, parsed!.Status);
        Assert.Equal(0x3F, parsed.TransactionId);
        Assert.Equal(0x02, parsed.DataSize);
        Assert.Equal(0x07, parsed.CommandClass);
        Assert.Equal(0x80, parsed.CommandId);
        Assert.Equal(0xC0, parsed.Arguments[1]);
    }

    [Fact]
    public void Parse_ReturnsNullForTooShort()
    {
        Assert.Null(RazerReport.Parse(new byte[90]));
    }

    [Fact]
    public void Command_DefaultsTransactionIdTo0()
    {
        // Per OpenRazer's get_razer_report — device-specific code overrides.
        var report = RazerReport.Command(0x00, 0x00, 0x00, Array.Empty<byte>());
        Assert.Equal(0x00, report.TransactionId);
    }

    [Fact]
    public void Command_TruncatesArgsToEighty()
    {
        var longArgs = new byte[200];
        for (var i = 0; i < 200; i++) longArgs[i] = 0xAA;
        var report = RazerReport.Command(0x00, 0x00, 0x00, longArgs);
        // Arguments buffer is fixed at 80 bytes; CopyTo won't overflow
        Assert.Equal(80, report.Arguments.Length);
        Assert.Equal(0xAA, report.Arguments[0]);
        Assert.Equal(0xAA, report.Arguments[79]);
    }
}
