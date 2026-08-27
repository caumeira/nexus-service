using System;
using System.Numerics;
using System.Security.Cryptography;
using Nexus.Service.Peripherals.Tryx.Panorama.Crypto;
using Xunit;

namespace Nexus.Service.Tests;

public class Sm2Tests
{
    // Self-consistent test keypair (d, Q=d*G) generated and verified offline
    // against sm2p256v1 - NOT the Kanali production keys. The two Kanali keys
    // embedded in TryxCloudCatalog are independently provisioned (the public
    // key encrypts to the server, the private key decrypts server replies to
    // this client) and are not a matching pair, so they cannot round-trip
    // against each other.
    private const string TestPrivateKeyHex = "001234567890abcdef1234567890abcdef1234567890abcdef1234567890abcd";
    private const string TestPublicKeyHex =
        "0417b937e7b5d2d00ceaec4ff25c6b74eaa57c8b67518fb630ec28ac04faf97f2471fa5c9eb79214b79568b2597a3c87fe8d3c0fb9a96b3cc36594bff402218fb1";

    private const string KanaliRequestPublicKeyHex =
        "04e4da9b393d64baff3294f9d57c2939596dfee21e0388b3c7688b9e066d28ce907dc933f45d7694c3b0beded76ae48a9295e49054fca75594d99a698a3c3ea294";

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("{\"code\":\"PANO_1011\"}")]
    [InlineData("a JSON body long enough to span multiple SM3 KDF blocks of 32 bytes each, repeated repeated repeated")]
    public void Encrypt_then_Decrypt_round_trips_with_matching_keypair(string plaintext)
    {
        var cipherHex = Sm2.Encrypt(plaintext, TestPublicKeyHex);

        var decrypted = Sm2.Decrypt(cipherHex, TestPrivateKeyHex);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypt_then_Decrypt_round_trips_with_04_prefixed_ciphertext()
    {
        // Kanali's own client prepends a literal "04" byte to its SM2 encrypt
        // output; Decrypt must accept that prefix transparently.
        var cipherHex = "04" + Sm2.Encrypt("prefixed C1 round trip", TestPublicKeyHex);

        var decrypted = Sm2.Decrypt(cipherHex, TestPrivateKeyHex);

        Assert.Equal("prefixed C1 round trip", decrypted);
    }

    // An ephemeral scalar whose C1 = k*G has x starting with the byte 0x04,
    // found offline by walking k*G on sm2p256v1. Pinned so this case is
    // deterministic: drawing k at random hits it at roughly 1-in-256 odds per
    // encrypt, and each miss costs two scalar multiplications.
    private const string RawC104LeadingEphemeralHex =
        "7f3a1c95d2b8e0461fa7c33d5e9b28417c60de85a1f492b3706c8ad159e2fc78";

    [Fact]
    public void Decrypt_round_trips_a_raw_C1_whose_x_coordinate_starts_with_0x04()
    {
        // A raw (unprefixed) C1 whose x coordinate's leading byte is 0x04 must
        // not be mistaken for a SEC1 04 tag byte and stripped.
        var k = new BigInteger(Convert.FromHexString(RawC104LeadingEphemeralHex), isUnsigned: true, isBigEndian: true);

        var cipherHex = Sm2.Encrypt("raw-c1-04-leading", TestPublicKeyHex, k);

        // Guards the pinned scalar: a k that stopped producing a 0x04-leading
        // C1 would otherwise make this test silently stop covering the case.
        Assert.StartsWith("04", cipherHex, StringComparison.Ordinal);
        Assert.Equal("raw-c1-04-leading", Sm2.Decrypt(cipherHex, TestPrivateKeyHex));
    }

    [Fact]
    public void Encrypt_output_is_well_formed_for_the_embedded_Kanali_public_key()
    {
        // The Kanali public key is real production data (decoded from the
        // desktop app bundle) and independently confirmed to lie on
        // sm2p256v1; this exercises the curve constants against it without
        // needing the matching private key (which Kanali does not expose).
        var plaintext = "{\"code\":\"PANO_1011\"}";

        var cipherHex = Sm2.Encrypt(plaintext, KanaliRequestPublicKeyHex);
        var cipherBytes = Convert.FromHexString(cipherHex);

        // C1 (64 bytes, no 04 prefix) + C3 (32 bytes) + C2 (plaintext length).
        Assert.Equal(64 + 32 + System.Text.Encoding.UTF8.GetByteCount(plaintext), cipherBytes.Length);
    }

    [Fact]
    public void Decrypt_throws_when_ciphertext_is_tampered()
    {
        var cipherHex = Sm2.Encrypt("integrity check", TestPublicKeyHex);
        var cipherBytes = Convert.FromHexString(cipherHex);
        cipherBytes[^1] ^= 0xFF;
        var tamperedHex = Convert.ToHexString(cipherBytes).ToLowerInvariant();

        Assert.Throws<CryptographicException>(() => Sm2.Decrypt(tamperedHex, TestPrivateKeyHex));
    }

    [Fact]
    public void Encrypt_produces_different_ciphertext_each_call()
    {
        var first = Sm2.Encrypt("same plaintext", TestPublicKeyHex);
        var second = Sm2.Encrypt("same plaintext", TestPublicKeyHex);

        Assert.NotEqual(first, second);
    }
}
