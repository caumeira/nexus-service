using Qos.Service.Lighting.Rgb;

namespace Qos.Service.Tests;

public class OpenRgbProtocolTests
{
    [Fact]
    public void WriteHeader_ProducesCorrectMagicAndFields()
    {
        var buf = new byte[OpenRgbProtocol.HeaderSize];
        OpenRgbProtocol.WriteHeader(buf, deviceIndex: 5, OpenRgbProtocol.PacketId.RgbControllerUpdateLeds, dataSize: 100);

        // Magic "ORGB"
        Assert.Equal((byte)'O', buf[0]);
        Assert.Equal((byte)'R', buf[1]);
        Assert.Equal((byte)'G', buf[2]);
        Assert.Equal((byte)'B', buf[3]);

        // device_index = 5 (little-endian)
        Assert.Equal(5, buf[4]);
        Assert.Equal(0, buf[5]);

        // packet_id = 1050 (little-endian) → 0x0000041A
        Assert.Equal(0x1A, buf[8]);
        Assert.Equal(0x04, buf[9]);

        // data_size = 100 (little-endian)
        Assert.Equal(100, buf[12]);
    }

    [Fact]
    public void ReadHeader_ParsesWriteHeaderOutput()
    {
        var buf = new byte[OpenRgbProtocol.HeaderSize];
        OpenRgbProtocol.WriteHeader(buf, deviceIndex: 42, OpenRgbProtocol.PacketId.RequestControllerData, dataSize: 4);

        var (idx, id, size) = OpenRgbProtocol.ReadHeader(buf);
        Assert.Equal(42u, idx);
        Assert.Equal(OpenRgbProtocol.PacketId.RequestControllerData, id);
        Assert.Equal(4u, size);
    }

    [Fact]
    public void ReadHeader_RejectsBadMagic()
    {
        var buf = new byte[OpenRgbProtocol.HeaderSize];
        buf[0] = (byte)'X';
        buf[1] = (byte)'X';
        buf[2] = (byte)'X';
        buf[3] = (byte)'X';

        Assert.Throws<InvalidOperationException>(() => OpenRgbProtocol.ReadHeader(buf));
    }

    [Fact]
    public void BuildSetClientNameBody_AppendsNul()
    {
        var body = OpenRgbProtocol.BuildSetClientNameBody("qos");
        // 'q' 'o' 's' '\0'
        Assert.Equal(4, body.Length);
        Assert.Equal((byte)'q', body[0]);
        Assert.Equal((byte)'o', body[1]);
        Assert.Equal((byte)'s', body[2]);
        Assert.Equal(0, body[3]);
    }

    [Fact]
    public void BuildProtocolVersionBody_WritesUInt32LittleEndian()
    {
        var body = OpenRgbProtocol.BuildProtocolVersionBody(4);
        Assert.Equal(4, body.Length);
        Assert.Equal(4, body[0]);
        Assert.Equal(0, body[1]);
        Assert.Equal(0, body[2]);
        Assert.Equal(0, body[3]);
    }

    [Fact]
    public void BuildUpdateLedsBody_LayoutMatchesProtocol()
    {
        var colors = new[]
        {
            new RgbColor(255, 0, 0),
            new RgbColor(0, 255, 0),
            new RgbColor(0, 0, 255),
        };

        var body = OpenRgbProtocol.BuildUpdateLedsBody(colors);

        // 4 (data_size) + 2 (led_count) + 3 LEDs * 4 bytes = 18
        Assert.Equal(18, body.Length);

        // data_size = 18
        Assert.Equal(18, body[0]);
        Assert.Equal(0, body[1]);

        // led_count = 3
        Assert.Equal(3, body[4]);
        Assert.Equal(0, body[5]);

        // LED 0: R=255, G=0, B=0, padding=0
        Assert.Equal(255, body[6]);
        Assert.Equal(0, body[7]);
        Assert.Equal(0, body[8]);
        Assert.Equal(0, body[9]);

        // LED 1: R=0, G=255, B=0, padding=0
        Assert.Equal(0, body[10]);
        Assert.Equal(255, body[11]);
        Assert.Equal(0, body[12]);
        Assert.Equal(0, body[13]);

        // LED 2: R=0, G=0, B=255, padding=0
        Assert.Equal(0, body[14]);
        Assert.Equal(0, body[15]);
        Assert.Equal(255, body[16]);
        Assert.Equal(0, body[17]);
    }

    [Fact]
    public void RgbColor_ScaleClampsBrightness()
    {
        var c = new RgbColor(200, 100, 50);
        Assert.Equal(RgbColor.Black.R, c.Scale(0).R);
        Assert.Equal(c.R, c.Scale(1).R);
        Assert.Equal(c.R, c.Scale(2).R); // Above 1 clamps to 1
    }

    [Fact]
    public void RgbColor_ScaleHalfHalvesChannels()
    {
        var c = new RgbColor(200, 100, 50);
        var dim = c.Scale(0.5);
        Assert.Equal(100, dim.R);
        Assert.Equal(50, dim.G);
        Assert.Equal(25, dim.B);
    }
}
