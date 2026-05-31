using System;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Platform.Linux.DBus;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Round-trips the exact xdg-desktop-portal ScreenCast wire formats the Linux
/// screen-mirror path depends on: the <c>a{sv}</c> options dict we send and the
/// <c>ua{sv}</c> Request.Response body we parse (including the nested
/// <c>streams: a(ua{sv})</c>). These are the fiddly, alignment-sensitive bits —
/// a marshalling regression here silently breaks capture, so we pin it with the
/// real <see cref="LinuxScreenCastPortal"/> helpers + <see cref="DBusWriter"/>/
/// <see cref="DBusReader"/>. The live PipeWire stream is verified on hardware.
/// </summary>
public class ScreenCastPortalMarshallingTests
{
    [Fact]
    public void Options_RoundTrip_PreservesEveryEntry()
    {
        var w = new DBusWriter();
        LinuxScreenCastPortal.WriteOptions(w,
            ("handle_token", "s", x => x.WriteString("nexus1")),
            ("types", "u", x => x.WriteUInt32(1)),
            ("multiple", "b", x => x.WriteBool(false)),
            ("cursor_mode", "u", x => x.WriteUInt32(2)),
            ("persist_mode", "u", x => x.WriteUInt32(2)),
            ("restore_token", "s", x => x.WriteString("tok-abc")));

        var dict = new DBusReader(w.ToArray()).ReadStringVariantDict();

        Assert.Equal("nexus1", dict["handle_token"]);
        Assert.Equal(1L, dict["types"]);          // 'u' boxes as long
        Assert.Equal(false, dict["multiple"]);    // 'b' boxes as bool
        Assert.Equal(2L, dict["cursor_mode"]);
        Assert.Equal(2L, dict["persist_mode"]);
        Assert.Equal("tok-abc", dict["restore_token"]);
    }

    [Fact]
    public void StartResponse_ParsesNodeIdAndRestoreToken()
    {
        var body = BuildStartResponse(response: 0, nodeId: 42, restoreToken: "rt-xyz");

        var r = new DBusReader(body);
        var response = r.ReadUInt32();
        var results = r.ReadStringVariantDict();

        Assert.Equal(0u, response);
        Assert.True(LinuxScreenCastPortal.TryGetNodeId(results, out var node));
        Assert.Equal(42u, node);
        Assert.True(LinuxScreenCastPortal.TryGetString(results, "restore_token", out var tok));
        Assert.Equal("rt-xyz", tok);
    }

    [Fact]
    public void TryGetNodeId_ReturnsFalse_WhenNoStreams()
    {
        var d = new System.Collections.Generic.Dictionary<string, object?>
        {
            ["restore_token"] = "only-a-token",
        };
        Assert.False(LinuxScreenCastPortal.TryGetNodeId(d, out var node));
        Assert.Equal(0u, node);
    }

    [Fact]
    public void StartResponse_UsesFirstStream_WhenMultiplePresent()
    {
        var body = BuildStartResponse(response: 0, nodeId: 7, restoreToken: "", extraNodeId: 99);
        var r = new DBusReader(body);
        _ = r.ReadUInt32();
        var results = r.ReadStringVariantDict();
        Assert.True(LinuxScreenCastPortal.TryGetNodeId(results, out var node));
        Assert.Equal(7u, node);
    }

    // Marshals a portal Start Response body: u response + a{sv} { streams:
    // a(ua{sv}), [restore_token: s] }. Mirrors what xdg-desktop-portal sends.
    private static byte[] BuildStartResponse(uint response, uint nodeId, string restoreToken, uint? extraNodeId = null)
    {
        var w = new DBusWriter();
        w.WriteUInt32(response);
        w.OpenArray(alignment: 8); // a{sv} results

        w.AlignTo(8);
        w.WriteString("streams");
        w.WriteVariant("a(ua{sv})", v =>
        {
            v.OpenArray(alignment: 8); // array of (ua{sv}) structs
            WriteStream(v, nodeId);
            if (extraNodeId is { } extra)
                WriteStream(v, extra);
            v.CloseArray();
        });

        if (!string.IsNullOrEmpty(restoreToken))
        {
            w.AlignTo(8);
            w.WriteString("restore_token");
            w.WriteVariant("s", v => v.WriteString(restoreToken));
        }

        w.CloseArray();
        return w.ToArray();
    }

    private static void WriteStream(DBusWriter v, uint nodeId)
    {
        v.AlignTo(8);          // struct boundary
        v.WriteUInt32(nodeId); // u
        v.OpenArray(alignment: 8); // a{sv} props (empty)
        v.CloseArray();
    }
}
