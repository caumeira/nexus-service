using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests;

public class DeviceFrameTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var frame = new DeviceFrame(0, "test", 16, x: 100, y: 200, w: 300, h: 40);
        Assert.Equal(16, frame.LedCount);
        Assert.Equal(100, frame.X);
        Assert.Equal(200, frame.Y);
        Assert.Equal(300, frame.W);
        Assert.Equal(40, frame.H);
    }

    [Fact]
    public void SetLed_WritesCorrectRGB()
    {
        var frame = new DeviceFrame(0, "test", 4);
        frame.SetLed(0, 255, 0, 0);
        frame.SetLed(1, 0, 255, 0);
        frame.SetLed(2, 0, 0, 255);
        frame.SetLed(3, 128, 64, 32);

        var bytes = frame.LedBytes;
        Assert.Equal(255, bytes[0]); Assert.Equal(0, bytes[1]); Assert.Equal(0, bytes[2]);
        Assert.Equal(0, bytes[3]); Assert.Equal(255, bytes[4]); Assert.Equal(0, bytes[5]);
        Assert.Equal(0, bytes[6]); Assert.Equal(0, bytes[7]); Assert.Equal(255, bytes[8]);
        Assert.Equal(128, bytes[9]); Assert.Equal(64, bytes[10]); Assert.Equal(32, bytes[11]);
    }

    [Fact]
    public void SetLed_OutOfRange_DoesNotThrow()
    {
        var frame = new DeviceFrame(0, "test", 2);
        frame.SetLed(-1, 255, 255, 255);
        frame.SetLed(2, 255, 255, 255);
        frame.SetLed(100, 255, 255, 255);

        var bytes = frame.LedBytes;
        for (int i = 0; i < bytes.Length; i++)
            Assert.Equal(0, bytes[i]);
    }

    [Fact]
    public void Fill_SetsAllLeds()
    {
        var frame = new DeviceFrame(0, "test", 4);
        frame.Fill(100, 200, 50);

        var bytes = frame.LedBytes;
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(100, bytes[i * 3]);
            Assert.Equal(200, bytes[i * 3 + 1]);
            Assert.Equal(50, bytes[i * 3 + 2]);
        }
    }

    [Fact]
    public void Clear_ZerosAllLeds()
    {
        var frame = new DeviceFrame(0, "test", 4);
        frame.Fill(255, 255, 255);
        frame.Clear();

        var bytes = frame.LedBytes;
        for (int i = 0; i < bytes.Length; i++)
            Assert.Equal(0, bytes[i]);
    }
}
