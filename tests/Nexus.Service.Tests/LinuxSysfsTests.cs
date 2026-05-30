using System.IO;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>Edge cases for the shared sysfs helper that backs the Linux providers.</summary>
public class LinuxSysfsTests
{
    [Fact]
    public void ReadText_TrimsContent_NullWhenMissing()
    {
        using var t = new TempDir();
        var p = t.Write("attr", "  hello\n");
        Assert.Equal("hello", LinuxSysfs.ReadText(p));
        Assert.Null(LinuxSysfs.ReadText(t.At("nope")));
    }

    [Fact]
    public void ReadInt_ParsesDecimal_NullOnGarbage()
    {
        using var t = new TempDir();
        Assert.Equal(255, LinuxSysfs.ReadInt(t.Write("pwm", "255\n")));
        Assert.Null(LinuxSysfs.ReadInt(t.Write("bad", "not-a-number")));
        Assert.Null(LinuxSysfs.ReadInt(t.At("missing")));
    }

    [Fact]
    public void ReadHex_ParsesNoPrefixHex_NullOnGarbage()
    {
        using var t = new TempDir();
        Assert.Equal(0x3402, LinuxSysfs.ReadHex(t.Write("idVendor", "3402\n")));
        Assert.Equal(0x0901, LinuxSysfs.ReadHex(t.Write("idProduct", "0901")));
        Assert.Null(LinuxSysfs.ReadHex(t.Write("nothex", "zzzz")));
    }

    [Fact]
    public void WriteText_WritesExactly_FalseOnBadPath()
    {
        using var t = new TempDir();
        var p = t.At("out");
        Assert.True(LinuxSysfs.WriteText(p, "128"));
        Assert.Equal("128", File.ReadAllText(p));
        // a path whose parent dir does not exist cannot be written
        Assert.False(LinuxSysfs.WriteText(t.At("no/such/dir/file"), "x"));
    }
}
