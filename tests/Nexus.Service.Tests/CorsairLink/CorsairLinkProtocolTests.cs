using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkProtocolTests
{
    [Fact]
    public void Constants_match_known_values()
    {
        Assert.Equal(0x1B1C, CorsairLinkProtocol.VendorId);
        Assert.Equal(0x0C3F, CorsairLinkProtocol.ProductId);
        Assert.Equal(0xFF42, CorsairLinkProtocol.VendorUsagePage);
        Assert.Equal(0x01, CorsairLinkProtocol.VendorUsage);
        Assert.Equal(512, CorsairLinkProtocol.ReportLength);
        Assert.Equal(513, CorsairLinkProtocol.WriteBufferLength);
        Assert.Equal(3, CorsairLinkProtocol.HeaderSize);
    }

    // Captured live from the Y70 hub (firmware 3.2.571, 3x iCUE LINK QX RGB).
    // getSpeeds response prefix: header[0..3], dataType 25 00, amount=0x1A,
    // then 3-byte sensors [status, lo, hi]. s0 reserved (status 1); fans at 1/2/3.
    private static readonly byte[] SpeedsResponse =
    {
        0x00, 0x00, 0x08, 0x00, 0x25, 0x00, 0x1A,
        0x01, 0x00, 0x00,   // s0: status 1 -> invalid
        0x00, 0xE0, 0x01,   // s1: 0x01E0 = 480 rpm
        0x00, 0xE0, 0x01,   // s2: 480 rpm
    };

    [Fact]
    public void ParseSpeeds_decodes_captured_hub_bytes()
    {
        var rpm = new int[CorsairLinkProtocol.SensorArrayLength];
        Array.Fill(rpm, -99);
        CorsairLinkProtocol.ParseSpeeds(SpeedsResponse, rpm);

        Assert.Equal(-1, rpm[0]);   // reserved hub slot, status != 0
        Assert.Equal(480, rpm[1]);
        Assert.Equal(480, rpm[2]);
        Assert.Equal(-99, rpm[3]);  // past the captured prefix: left untouched
    }

    // getTemperatures response prefix: dataType 10 00, amount=0x1A, then 3-byte
    // sensors [status, lo, hi] in tenths of a degree.
    private static readonly byte[] TempsResponse =
    {
        0x00, 0x00, 0x08, 0x00, 0x10, 0x00, 0x1A,
        0x01, 0x00, 0x00,   // s0: status 1 -> NaN
        0x00, 0x1D, 0x01,   // s1: 0x011D = 285 -> 28.5 C
        0x00, 0x18, 0x01,   // s2: 0x0118 = 280 -> 28.0 C
    };

    [Fact]
    public void ParseTemperatures_decodes_captured_hub_bytes()
    {
        var temp = new float[CorsairLinkProtocol.SensorArrayLength];
        Array.Fill(temp, -99f);
        CorsairLinkProtocol.ParseTemperatures(TempsResponse, temp);

        Assert.True(float.IsNaN(temp[0]));
        Assert.Equal(28.5f, temp[1], 3);
        Assert.Equal(28.0f, temp[2], 3);
        Assert.Equal(-99f, temp[3], 3);
    }

    [Fact]
    public void ParseDevices_enumerates_three_qx_fans()
    {
        var buf = BuildDevicesResponse(new (int type, int model, string serial)[]
        {
            (1, 0, "AAAA"),
            (1, 0, "BBBB"),
            (1, 0, "CCCC"),
        });

        var devices = CorsairLinkProtocol.ParseDevices(buf);

        Assert.Equal(3, devices.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i + 1, devices[i].Channel);   // 1-based daisy-chain order
            Assert.Equal(1, devices[i].Type);
            Assert.Equal(0, devices[i].Model);
        }
        Assert.Equal("AAAA", devices[0].Serial);
        Assert.Equal("CCCC", devices[2].Serial);
    }

    [Fact]
    public void ParseDevices_skips_empty_slots()
    {
        // channels=2 but the second slot has a zero-length id (empty).
        var buf = new byte[64];
        buf[2] = 0x08;
        buf[4] = 0x21;
        buf[6] = 2;            // channels
        var pos = 7;
        // slot 1: a QX fan with a 2-byte serial
        buf[pos + 2] = 1;     // type
        buf[pos + 3] = 0;     // model
        buf[pos + 7] = 2;     // idLen
        buf[pos + 8] = (byte)'X';
        buf[pos + 9] = (byte)'Y';
        pos += 8 + 2;
        // slot 2: empty (idLen 0)
        buf[pos + 7] = 0;

        var devices = CorsairLinkProtocol.ParseDevices(buf);

        Assert.Single(devices);
        Assert.Equal(1, devices[0].Channel);
        Assert.Equal("XY", devices[0].Serial);
    }

    [Theory]
    [InlineData(1, 0, "iCUE LINK QX RGB", 34, CorsairLinkClass.Fan, true, true)]
    [InlineData(3, 0, "iCUE LINK RX RGB MAX", 8, CorsairLinkClass.Fan, true, false)]
    [InlineData(12, 0, "iCUE LINK XD5 Elite", 22, CorsairLinkClass.Pump, true, true)]
    public void Models_lookup_known_devices(int type, int model, string name, int leds, CorsairLinkClass cls, bool speed, bool temp)
    {
        var m = CorsairLinkModels.Lookup(type, model);
        Assert.Equal(name, m.Name);
        Assert.Equal(leds, m.LedCount);
        Assert.Equal(cls, m.Class);
        Assert.Equal(speed, m.HasSpeed);
        Assert.Equal(temp, m.HasTemperature);
    }

    [Fact]
    public void Models_lookup_unknown_type_falls_back_to_other()
    {
        var m = CorsairLinkModels.Lookup(0xEE, 0x00);
        Assert.Equal(CorsairLinkClass.Other, m.Class);
    }

    [Fact]
    public void Models_lookup_unknown_model_of_known_type_uses_base()
    {
        // A new QX revision (type 1, unmapped model) still resolves as a QX fan.
        var m = CorsairLinkModels.Lookup(1, 9);
        Assert.Equal(CorsairLinkClass.Fan, m.Class);
        Assert.Equal(34, m.LedCount);
    }

    private static byte[] BuildDevicesResponse(IReadOnlyList<(int type, int model, string serial)> devices)
    {
        var bytes = new List<byte> { 0x00, 0x00, 0x08, 0x00, 0x21, 0x00, (byte)devices.Count };
        foreach (var (type, model, serial) in devices)
        {
            var rec = new byte[8];
            rec[2] = (byte)type;
            rec[3] = (byte)model;
            rec[7] = (byte)serial.Length;
            bytes.AddRange(rec);
            foreach (var c in serial) bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }
}
