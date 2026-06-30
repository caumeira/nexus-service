using Nexus.Service.Media;

namespace Nexus.Service.Tests;

public class MediaImporterSanitizeIdTests
{
    [Theory]
    [InlineData("18f2-Keyboard_Glow", "18f2-Keyboard_Glow")]
    [InlineData("abc_DEF-123", "abc_DEF-123")]
    public void SanitizeId_LeavesAsciiUnchanged(string input, string expected)
    {
        Assert.Equal(expected, MediaImporter.SanitizeId(input));
    }

    [Theory]
    [InlineData("19f1-日本語")] // 日本語
    [InlineData("19f1-café")]           // café
    [InlineData("19f1-😀")]        // 😀 (surrogate pair)
    [InlineData("19f1-рус")]  // Cyrillic
    public void SanitizeId_ProducesPureAscii(string input)
    {
        var id = MediaImporter.SanitizeId(input);
        Assert.All(id, ch => Assert.True(ch < 128, $"non-ASCII char in id: {id}"));
        Assert.True(MediaLibrary.IsValidId(id), $"sanitized id rejected by IsValidId: {id}");
    }

    [Fact]
    public void SanitizeId_NonAsciiBecomesUnderscore()
    {
        Assert.Equal("19f1-___", MediaImporter.SanitizeId("19f1-日本語"));
    }

    [Fact]
    public void SanitizeId_CapsAtSixtyFourCharsWithoutSplittingSurrogates()
    {
        // A long all-emoji name truncated at 64 must not leave a lone surrogate.
        var input = string.Concat(Enumerable.Repeat("😀", 100));
        var id = MediaImporter.SanitizeId(input);
        Assert.True(id.Length <= 64);
        Assert.DoesNotContain(id, char.IsSurrogate);
    }
}
