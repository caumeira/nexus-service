using System;
using Nexus.Service.Lighting.Smart.Drivers.Hue;
using Xunit;

namespace Nexus.Service.Tests.SmartLights;

/// <summary>
/// Validates the hand-rolled DTLS crypto primitives against known vectors so a
/// bug surfaces here, not as an opaque handshake failure against the bridge.
/// </summary>
public class DtlsPskCryptoTests
{
    // Canonical TLS 1.2 PRF (P_SHA256) test vector — widely published
    // (IETF TLS WG list). PRF(secret, "test label", seed) → 100 bytes.
    [Fact]
    public void Prf_matchesTls12Sha256Vector()
    {
        var secret = Convert.FromHexString("9bbe436ba940f017b17652849a71db35");
        var seed = Convert.FromHexString("a0ba9f936cda311827a6f796ffd5198c");
        const string expected =
            "e3f229ba727be17b8d122620557cd453" +
            "c2aab21d07c3d495329b52d4e61edb5a" +
            "6b301791e90d35c9c9a46b4e14baf9af" +
            "0fa022f7077def17abfd3797c0564bab" +
            "4fbc91666e9def9b97fce34f796789ba" +
            "a48082d122ee42c5a72e5a5110fff701" +
            "87347b66";

        var got = DtlsPskClient.Prf(secret, "test label", seed, 100);
        Assert.Equal(expected.ToUpperInvariant(), Convert.ToHexString(got));
    }

    [Fact]
    public void PskPremaster_hasRfc4279Structure()
    {
        // psk = {01,02,03} (N=3) → 0003 000000 0003 010203
        var pm = DtlsPskClient.PskPremaster(new byte[] { 0x01, 0x02, 0x03 });
        Assert.Equal("0003000000000301 0203".Replace(" ", "").ToUpperInvariant(), Convert.ToHexString(pm));
    }

    [Fact]
    public void PskPremaster_lengthIsDoublePskPlusFour()
    {
        var pm = DtlsPskClient.PskPremaster(new byte[16]); // 16-byte psk
        Assert.Equal(2 + 16 + 2 + 16, pm.Length);
    }
}
