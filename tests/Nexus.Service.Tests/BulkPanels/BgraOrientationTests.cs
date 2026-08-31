using Nexus.Service.Peripherals.PixelFormats;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BgraOrientationTests
{
    // A 2x2 frame whose pixels carry their own index in every channel, so a transform is
    // readable as the order the indices come back in.
    private static byte[] Indexed(int width, int height)
    {
        var frame = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            frame[i * 4] = frame[(i * 4) + 1] = frame[(i * 4) + 2] = frame[(i * 4) + 3] = (byte)i;
        }
        return frame;
    }

    private static int[] Order(byte[] frame) =>
        System.Linq.Enumerable.Range(0, frame.Length / 4).Select(i => (int)frame[i * 4]).ToArray();

    [Theory]
    [InlineData(false, false, new[] { 0, 1, 2, 3 })]
    [InlineData(true, false, new[] { 3, 2, 1, 0 })]   // 180: reverse
    [InlineData(false, true, new[] { 1, 0, 3, 2 })]   // mirror: reverse within each row
    [InlineData(true, true, new[] { 2, 3, 0, 1 })]    // both: reverse row order only
    public void Transforms_compose_as_a_row_and_column_flip(bool flip, bool mirror, int[] expected)
    {
        var src = Indexed(2, 2);
        var dest = new byte[src.Length];

        BgraOrientation.Apply(src, 2, 2, flip, mirror, dest);

        Assert.Equal(expected, Order(dest));
    }

    [Fact]
    public void Identity_is_only_the_untransformed_case()
    {
        Assert.True(BgraOrientation.IsIdentity(false, false));
        Assert.False(BgraOrientation.IsIdentity(true, false));
        Assert.False(BgraOrientation.IsIdentity(false, true));
        // Flip plus mirror is a vertical flip, not a no-op.
        Assert.False(BgraOrientation.IsIdentity(true, true));
    }

    [Fact]
    public void Applying_a_transform_twice_returns_the_original()
    {
        var src = Indexed(4, 3);
        var once = new byte[src.Length];
        var twice = new byte[src.Length];

        BgraOrientation.Apply(src, 4, 3, flip180: true, mirror: true, once);
        BgraOrientation.Apply(once, 4, 3, flip180: true, mirror: true, twice);

        Assert.Equal(src, twice);
    }

    [Fact]
    public void Rgb565_mirrors_on_the_read_index_without_a_second_pass()
    {
        var src = Indexed(2, 1);
        var plain = new byte[Rgb565Encoder.EncodedLength(2, 1)];
        var mirrored = new byte[plain.Length];

        Rgb565Encoder.Encode(src, 2, 1, quarterTurns: 0, plain, sourceIsBgra: true);
        Rgb565Encoder.Encode(src, 2, 1, quarterTurns: 0, mirrored, sourceIsBgra: true, mirror: true);

        Assert.Equal(new[] { plain[2], plain[3], plain[0], plain[1] }, mirrored);
    }
}
