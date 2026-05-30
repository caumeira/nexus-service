using System.Collections.Generic;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Covers the MPRIS-props → MediaSession mapping (BuildSession), the logic between the D-Bus read and the UI.</summary>
public class LinuxMediaProviderTests
{
    [Fact]
    public void BuildSession_MapsMprisPropsToSession()
    {
        var meta = new Dictionary<string, object?>
        {
            ["xesam:title"] = "Best Song",
            ["xesam:artist"] = new List<object?> { "Artist One", "Artist Two" },
            ["xesam:album"] = "The Album",
            ["mpris:length"] = 240_000_000L, // microseconds
        };
        var props = new Dictionary<string, object?>
        {
            ["PlaybackStatus"] = "Playing",
            ["Position"] = 42_000_000L,
            ["Metadata"] = meta,
            ["CanGoNext"] = true,
            ["CanGoPrevious"] = false,
            ["CanPlay"] = true,
            ["CanPause"] = true,
            ["CanSeek"] = true,
            ["Shuffle"] = true,
            ["LoopStatus"] = "Track",
        };

        var s = LinuxMediaProvider.BuildSession("org.mpris.MediaPlayer2.spotify", props, canFocus: true);

        Assert.Equal("Spotify", s.SourceAppName);
        Assert.True(s.IsFocused); // canFocus && Playing
        Assert.Equal("Best Song", s.Song.Title);
        Assert.Equal("Artist One, Artist Two", s.Song.Artist);
        Assert.Equal("The Album", s.Song.Album);
        Assert.True(s.Playback.Playing);
        Assert.False(s.Playback.Stopped);
        Assert.True(s.Playback.Shuffled);
        Assert.Equal("Track", s.Playback.RepeatMode);
        Assert.Equal(42_000.0, s.Playback.PositionMs);  // µs → ms
        Assert.Equal(240_000.0, s.Playback.DurationMs);
        Assert.True(s.Controls.IsNextEnabled);
        Assert.False(s.Controls.IsPrevEnabled);
    }

    [Fact]
    public void BuildSession_StoppedNotFocused_AndStripsInstanceSuffix()
    {
        var props = new Dictionary<string, object?> { ["PlaybackStatus"] = "Stopped" };
        var s = LinuxMediaProvider.BuildSession("org.mpris.MediaPlayer2.vlc.instance7", props, canFocus: true);
        Assert.Equal("Vlc", s.SourceAppName); // first segment after the prefix, capitalized
        Assert.False(s.IsFocused);
        Assert.True(s.Playback.Stopped);
    }
}
