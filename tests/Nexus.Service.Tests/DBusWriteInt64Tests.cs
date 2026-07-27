using System;
using Nexus.Service.Platform.Linux.DBus;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// DBusWriter.WriteInt64 backs MPRIS SetPosition(o TrackId, x Position).
/// D-Bus `x` is 8-byte aligned, so a preceding odd-sized field must be padded
/// or the player reads a garbage position.
/// </summary>
public sealed class DBusWriteInt64Tests
{
    [Fact]
    public void WriteInt64_emits_eight_little_endian_bytes()
    {
        var w = new DBusWriter();
        w.WriteInt64(0x0102030405060708L);

        var bytes = w.ToArray();

        Assert.Equal(8, bytes.Length);
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, bytes);
    }

    [Fact]
    public void WriteInt64_aligns_to_an_eight_byte_boundary()
    {
        var w = new DBusWriter();
        w.WriteByte(0xAB);
        w.WriteInt64(1);

        var bytes = w.ToArray();

        // 1 byte of payload + 7 pad + 8 for the int64.
        Assert.Equal(16, bytes.Length);
        Assert.Equal(0xAB, bytes[0]);
        for (var i = 1; i < 8; i++) Assert.Equal(0, bytes[i]);
        Assert.Equal(1, bytes[8]);
    }

    [Fact]
    public void WriteInt64_round_trips_the_microsecond_range_a_seek_uses()
    {
        // A 24h track in microseconds comfortably exceeds int32.
        const long micros = 24L * 60 * 60 * 1000 * 1000;
        var w = new DBusWriter();
        w.WriteInt64(micros);

        Assert.Equal(micros, BitConverter.ToInt64(w.ToArray(), 0));
    }
}
