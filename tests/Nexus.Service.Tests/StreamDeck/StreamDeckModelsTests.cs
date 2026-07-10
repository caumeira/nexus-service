using System.Linq;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Sanity checks over the plan streamdeck-support.md §1 capability table
/// (14 button-only models), cross-verified against the MIT elgato-streamdeck
/// crate's src/info.rs (fetched 2026-07-10).
/// </summary>
public class StreamDeckModelsTests
{
    [Fact]
    public void All_HasFourteenModels()
    {
        Assert.Equal(14, StreamDeckModels.All.Count);
    }

    [Fact]
    public void All_ProductIdsAreUnique()
    {
        var dups = StreamDeckModels.All.GroupBy(m => m.ProductId).Where(g => g.Count() > 1).ToList();
        Assert.Empty(dups);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void Layout_RowsTimesColumns_MatchesKeyCount(StreamDeckModel model)
    {
        Assert.Equal(model.KeyCount, model.Rows * model.Columns);
    }

    [Fact]
    public void OnlyMiniIsVerified()
    {
        var verified = StreamDeckModels.All.Where(m => m.Verified).Select(m => m.ProductId).ToList();
        Assert.Equal(new[] { 0x0063 }, verified);
    }

    [Fact]
    public void ByProductId_FindsMini()
    {
        var model = StreamDeckModels.ByProductId(0x0063);
        Assert.NotNull(model);
        Assert.Equal("Stream Deck Mini", model!.Name);
    }

    [Fact]
    public void ByProductId_UnknownReturnsNull()
    {
        Assert.Null(StreamDeckModels.ByProductId(0xDEAD));
    }

    [Fact]
    public void Pedal_HasNoKeyImage()
    {
        var pedal = StreamDeckModels.ByProductId(0x0086)!;
        Assert.Equal(StreamDeckImageFormat.None, pedal.ImageFormat);
        Assert.Equal(0, pedal.KeyPixelSize);
    }

    [Fact]
    public void OnlyOriginal_HasRightToLeftRemap()
    {
        var withRemap = StreamDeckModels.All.Where(m => m.KeyIndexRightToLeft).Select(m => m.ProductId).ToList();
        Assert.Equal(new[] { 0x0060 }, withRemap);
    }

    [Fact]
    public void FlipWithinRow_IsSelfInverse()
    {
        for (var col = 0; col < 5; col++)
        {
            var flipped = StreamDeckModels.FlipWithinRow(col, 5);
            Assert.Equal(col, StreamDeckModels.FlipWithinRow(flipped, 5));
        }
    }

    [Fact]
    public void FlipWithinRow_ReversesEachRow()
    {
        // 3x5 grid: row 0 is keys 0..4, so key 0 <-> key 4, key 1 <-> key 3, key 2 stays.
        Assert.Equal(4, StreamDeckModels.FlipWithinRow(0, 5));
        Assert.Equal(3, StreamDeckModels.FlipWithinRow(1, 5));
        Assert.Equal(2, StreamDeckModels.FlipWithinRow(2, 5));
        Assert.Equal(1, StreamDeckModels.FlipWithinRow(3, 5));
        Assert.Equal(0, StreamDeckModels.FlipWithinRow(4, 5));
    }

    public static IEnumerable<object[]> Models() => StreamDeckModels.All.Select(m => new object[] { m });
}
