using Nexus.Service.Peripherals.Protocols.Razer;

namespace Nexus.Service.Tests;

/// <summary>
/// A device reply can be corrupted in transit; <see cref="RazerReport.Parse"/>
/// now recomputes the XOR CRC and flags a mismatch via
/// <see cref="RazerReport.CrcValid"/> instead of silently trusting the bytes.
/// </summary>
public sealed class RazerReplyCrcTests
{
    private static byte[] WellFormedReply()
        => RazerReport.Command(0x07, 0x80, 0x02, new byte[] { 0x12, 0x34 }).ToHidFeatureBuffer();

    [Fact]
    public void Marks_well_formed_reply_crc_valid()
    {
        var parsed = RazerReport.Parse(WellFormedReply());

        Assert.NotNull(parsed);
        Assert.True(parsed!.CrcValid);
    }

    [Fact]
    public void Flags_corrupted_argument_byte()
    {
        var buf = WellFormedReply();
        buf[20] ^= 0xFF; // flip an argument byte (inside the CRC range)

        var parsed = RazerReport.Parse(buf);

        Assert.NotNull(parsed);
        Assert.False(parsed!.CrcValid);
    }

    [Fact]
    public void Flags_corrupted_crc_byte()
    {
        var buf = WellFormedReply();
        buf[89] ^= 0xFF; // flip the stored CRC itself

        var parsed = RazerReport.Parse(buf);

        Assert.NotNull(parsed);
        Assert.False(parsed!.CrcValid);
    }
}
