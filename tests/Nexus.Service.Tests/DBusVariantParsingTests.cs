using System.Collections.Generic;
using Nexus.Service.Platform.Linux.DBus;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Validates the D-Bus variant reader extensions that back LinuxMediaProvider's
/// MPRIS parsing. Payloads are encoded with the production DBusWriter (the same
/// marshaller used on the wire) and parsed back, so a round-trip failure points
/// at a real alignment / type bug. Runs on any OS (pure byte manipulation).
/// </summary>
public class DBusVariantParsingTests
{
    private static void WriteInt64(DBusWriter w, long v)
    {
        w.AlignTo(8);
        for (var i = 0; i < 8; i++)
            w.WriteByte((byte)(v >> (8 * i)));
    }

    [Fact]
    public void ReadStringArray_ParsesListNamesReply()
    {
        var w = new DBusWriter();
        w.OpenArray(4);
        w.WriteString("org.mpris.MediaPlayer2.spotify");
        w.WriteString("org.freedesktop.DBus");
        w.WriteString("org.mpris.MediaPlayer2.firefox.instance_1");
        w.CloseArray();

        var names = new DBusReader(w.ToArray()).ReadStringArray();

        Assert.Equal(3, names.Count);
        Assert.Equal("org.mpris.MediaPlayer2.spotify", names[0]);
        Assert.Contains("org.mpris.MediaPlayer2.firefox.instance_1", names);
    }

    [Fact]
    public void ReadStringVariantDict_ParsesMprisGetAllPayload()
    {
        // Mimics Properties.GetAll("org.mpris.MediaPlayer2.Player") - a{sv} with
        // scalars, an array-of-string, and a nested a{sv} Metadata dict.
        var w = new DBusWriter();
        w.OpenArray(8);

        w.AlignTo(8);
        w.WriteString("PlaybackStatus");
        w.WriteVariant("s", x => x.WriteString("Playing"));

        w.AlignTo(8);
        w.WriteString("CanGoNext");
        w.WriteVariant("b", x => x.WriteBool(true));

        w.AlignTo(8);
        w.WriteString("Position");
        w.WriteVariant("x", x => WriteInt64(x, 42_000_000L));

        w.AlignTo(8);
        w.WriteString("Metadata");
        w.WriteVariant("a{sv}", x =>
        {
            x.OpenArray(8);

            x.AlignTo(8);
            x.WriteString("xesam:title");
            x.WriteVariant("s", y => y.WriteString("Best Song"));

            x.AlignTo(8);
            x.WriteString("xesam:artist");
            x.WriteVariant("as", y =>
            {
                y.OpenArray(4);
                y.WriteString("Artist One");
                y.WriteString("Artist Two");
                y.CloseArray();
            });

            x.AlignTo(8);
            x.WriteString("mpris:length");
            x.WriteVariant("x", y => WriteInt64(y, 240_000_000L));

            x.AlignTo(8);
            x.WriteString("mpris:artUrl");
            x.WriteVariant("s", y => y.WriteString("file:///tmp/art.png"));

            x.CloseArray();
        });

        w.CloseArray();

        var dict = new DBusReader(w.ToArray()).ReadStringVariantDict();

        Assert.Equal("Playing", dict["PlaybackStatus"]);
        Assert.Equal(true, dict["CanGoNext"]);
        Assert.Equal(42_000_000L, dict["Position"]);

        var meta = Assert.IsType<Dictionary<string, object?>>(dict["Metadata"]);
        Assert.Equal("Best Song", meta["xesam:title"]);
        Assert.Equal("file:///tmp/art.png", meta["mpris:artUrl"]);
        Assert.Equal(240_000_000L, meta["mpris:length"]);

        var artists = Assert.IsType<List<object?>>(meta["xesam:artist"]);
        Assert.Equal(2, artists.Count);
        Assert.Equal("Artist One", artists[0]);
        Assert.Equal("Artist Two", artists[1]);
    }

    [Fact]
    public void ReadStringVariantDict_EmptyArray_YieldsEmptyDict()
    {
        var w = new DBusWriter();
        w.OpenArray(8);
        w.CloseArray();

        var dict = new DBusReader(w.ToArray()).ReadStringVariantDict();

        Assert.Empty(dict);
    }
}
