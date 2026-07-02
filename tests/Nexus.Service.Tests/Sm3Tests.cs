using System;
using System.Text;
using Nexus.Service.Peripherals.Tryx.Panorama.Crypto;
using Xunit;

namespace Nexus.Service.Tests;

public class Sm3Tests
{
    // GM/T 0004-2012 Appendix A.1 known-answer test.
    [Fact]
    public void Hash_abc_matches_gmt0004_test_vector()
    {
        var expected = Convert.FromHexString("66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0");

        var actual = Sm3.Hash(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hash_returns_32_bytes_for_empty_input()
    {
        var actual = Sm3.Hash(ReadOnlySpan<byte>.Empty);

        Assert.Equal(32, actual.Length);
    }

    [Fact]
    public void Hash_is_deterministic()
    {
        var input = Encoding.UTF8.GetBytes("nexus-tryx-cloud-catalog");

        var first = Sm3.Hash(input);
        var second = Sm3.Hash(input);

        Assert.Equal(first, second);
    }
}
