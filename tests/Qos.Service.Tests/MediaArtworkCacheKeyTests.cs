using Qos.Service.Activity;

namespace Qos.Service.Tests;

public class MediaArtworkCacheKeyTests
{
    [Theory]
    [InlineData("Next Song", "Artist", "Album")]
    [InlineData("Song", "Next Artist", "Album")]
    [InlineData("Song", "Artist", "Next Album")]
    public void Build_ChangesWhenTrackIdentityChanges(string title, string artist, string album)
    {
        var original = MediaArtworkCacheKey.Build("Spotify", "session", "Song", "Artist", "Album");
        var changed = MediaArtworkCacheKey.Build("Spotify", "session", title, artist, album);

        Assert.NotEqual(original, changed);
    }

    [Fact]
    public void Build_KeepsNullFieldsStable()
    {
        var withNulls = MediaArtworkCacheKey.Build("Spotify", "session", null, null, null);
        var withEmptyStrings = MediaArtworkCacheKey.Build("Spotify", "session", "", "", "");

        Assert.Equal(withEmptyStrings, withNulls);
    }
}
