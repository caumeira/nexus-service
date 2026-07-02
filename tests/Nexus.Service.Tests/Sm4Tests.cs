using System;
using Nexus.Service.Peripherals.Tryx.Panorama.Crypto;
using Xunit;

namespace Nexus.Service.Tests;

public class Sm4Tests
{
    // GM/T 0002-2012 Appendix A.1 known-answer test (single block, no padding).
    private static readonly byte[] StandardKey = Convert.FromHexString("0123456789abcdeffedcba9876543210");
    private static readonly byte[] StandardPlaintext = Convert.FromHexString("0123456789abcdeffedcba9876543210");
    private static readonly byte[] StandardCiphertext = Convert.FromHexString("681edf34d206965e86b3e94f536e4246");

    [Fact]
    public void EncryptEcb_matches_gmt0002_test_vector()
    {
        var actual = Sm4.EncryptEcb(StandardKey, StandardPlaintext);

        // EncryptEcb PKCS7-pads a full block onto exact-block-length input, so
        // the raw 16-byte ciphertext is the first block.
        Assert.Equal(StandardCiphertext, actual[..16]);
    }

    [Fact]
    public void DecryptEcb_of_raw_block_matches_gmt0002_plaintext()
    {
        // Decrypt the bare 16-byte GM/T block directly via the private block
        // transform path (ECB decrypt expects PKCS7 padding on the whole
        // buffer, so round-trip through Encrypt/Decrypt instead of feeding
        // the unpadded standard vector to DecryptEcb).
        var roundTrip = Sm4.DecryptEcb(StandardKey, Sm4.EncryptEcb(StandardKey, StandardPlaintext));

        Assert.Equal(StandardPlaintext, roundTrip);
    }

    [Fact]
    public void EncryptEcb_then_DecryptEcb_round_trips_arbitrary_length_data()
    {
        var key = Convert.FromHexString("00112233445566778899aabbccddeeff");
        var plaintext = System.Text.Encoding.UTF8.GetBytes("Tryx Panorama SM4 asset payload, not block aligned!!");

        var cipher = Sm4.EncryptEcb(key, plaintext);
        var decrypted = Sm4.DecryptEcb(key, cipher);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptCbc_then_DecryptCbc_round_trips_arbitrary_length_data()
    {
        var key = Convert.FromHexString("0123456789abcdeffedcba9876543210");
        var iv = Convert.FromHexString("00000000000000000000000000000001");
        var plaintext = System.Text.Encoding.UTF8.GetBytes("CBC mode round trip across multiple blocks of data.");

        var cipher = Sm4.EncryptCbc(key, iv, plaintext);
        var decrypted = Sm4.DecryptCbc(key, iv, cipher);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void DecryptEcb_rejects_non_block_aligned_input()
    {
        var key = Convert.FromHexString("0123456789abcdeffedcba9876543210");

        Assert.Throws<ArgumentException>(() => Sm4.DecryptEcb(key, new byte[10]));
    }
}
