using System;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Relay;

namespace Nexus.Service.Tests;

/// <summary>
/// Known-answer tests pinning the relay crypto contract to the exact hex
/// vectors in the relay-transport spec. These vectors are shared verbatim with
/// the WebCrypto implementation in nexus-web; if any assertion here changes, the
/// two ends have diverged and the channel will silently fail to decrypt.
/// </summary>
public class RelayCryptoTests
{
    // ── Spec vectors ────────────────────────────────────────────────────────
    private const string Token = "test-session-token-0123456789";
    private const string RelayRootHex = "36557d360330aad63010a257c870deb9f57c19d222ea630a9a204889eb435270";
    private const string Rid = "E5HaHgqqZJGdG5QZQR_LTQ";
    private const string ConnSaltHex = "000102030405060708090a0b0c0d0e0f";
    private const string AeadKeyHex = "2af213994553c206b634442d19b45b796710fd2e69efc060a9c10de16bb29e5f";
    private const string NonceHex = "010000000000000000000000";
    private const string Plaintext = "{\"t\":\"ping\",\"d\":1}";
    private const string CiphertextHex = "b62dce75421fdfc7760de887f5675c9f1aba";
    private const string TagHex = "87de074f45dc48f1bd17e54d146b70b3";
    private const string FrameHex =
        "010000000000000000000000b62dce75421fdfc7760de887f5675c9f1aba87de074f45dc48f1bd17e54d146b70b3";

    [Fact]
    public void DeriveRelayRoot_MatchesVector()
    {
        var root = RelayCrypto.DeriveRelayRoot(Token);
        Assert.Equal(RelayRootHex, Hex(root));
    }

    [Fact]
    public void DeriveRid_MatchesVector()
    {
        var root = FromHex(RelayRootHex);
        Assert.Equal(Rid, RelayCrypto.DeriveRid(root));
    }

    [Fact]
    public void DeriveAeadKey_MatchesVector()
    {
        var root = FromHex(RelayRootHex);
        var key = RelayCrypto.DeriveAeadKey(root, FromHex(ConnSaltHex));
        Assert.Equal(AeadKeyHex, Hex(key));
    }

    [Fact]
    public void Seal_ProducesVectorNonceCiphertextTagAndFrame()
    {
        var key = FromHex(AeadKeyHex);
        var plaintext = Encoding.UTF8.GetBytes(Plaintext);

        var frame = RelayCrypto.Seal(key, RelayCrypto.DirHostToClient, counter: 0, plaintext);

        // Whole frame must equal the pinned hex byte-for-byte.
        Assert.Equal(FrameHex, Hex(frame));

        // And each segment in isolation.
        var nonce = frame.AsSpan(0, RelayCrypto.NonceLength);
        var ciphertext = frame.AsSpan(RelayCrypto.NonceLength, plaintext.Length);
        var tag = frame.AsSpan(RelayCrypto.NonceLength + plaintext.Length, RelayCrypto.TagLength);
        Assert.Equal(NonceHex, Hex(nonce));
        Assert.Equal(CiphertextHex, Hex(ciphertext));
        Assert.Equal(TagHex, Hex(tag));
    }

    [Fact]
    public void Open_DecryptsVectorFrame()
    {
        var key = FromHex(AeadKeyHex);
        var (dir, counter, plaintext) = RelayCrypto.Open(key, FromHex(FrameHex));

        Assert.Equal(RelayCrypto.DirHostToClient, dir);
        Assert.Equal(0ul, counter);
        Assert.Equal(Plaintext, Encoding.UTF8.GetString(plaintext));
    }

    [Fact]
    public void SealOpen_RoundTrips_AcrossDirectionsAndCounters()
    {
        var key = RandomNumberGenerator.GetBytes(RelayCrypto.AeadKeyLength);
        var payload = Encoding.UTF8.GetBytes("{\"sub\":[\"processes\",\"network\"]}");

        foreach (var dir in new[] { RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost })
        {
            foreach (var counter in new ulong[] { 0, 1, 42, ulong.MaxValue })
            {
                var frame = RelayCrypto.Seal(key, dir, counter, payload);
                var (gotDir, gotCounter, gotPlain) = RelayCrypto.Open(key, frame);
                Assert.Equal(dir, gotDir);
                Assert.Equal(counter, gotCounter);
                Assert.Equal(payload, gotPlain);
            }
        }
    }

    [Fact]
    public void Open_RejectsTamperedTag()
    {
        var key = FromHex(AeadKeyHex);
        var frame = FromHex(FrameHex);
        // Flip one bit in the authentication tag (last byte).
        frame[^1] ^= 0x01;

        // AuthenticationTagMismatchException derives from CryptographicException;
        // assert on the base so the contract ("crypto failure ⇒ drop the frame")
        // holds regardless of the platform's concrete subclass.
        Assert.ThrowsAny<CryptographicException>(() => RelayCrypto.Open(key, frame));
    }

    [Fact]
    public void Open_RejectsTamperedCiphertext()
    {
        var key = FromHex(AeadKeyHex);
        var frame = FromHex(FrameHex);
        // Flip one bit inside the ciphertext body.
        frame[RelayCrypto.NonceLength] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => RelayCrypto.Open(key, frame));
    }

    [Fact]
    public void Open_RejectsWrongKey()
    {
        var wrongKey = RandomNumberGenerator.GetBytes(RelayCrypto.AeadKeyLength);
        Assert.ThrowsAny<CryptographicException>(() => RelayCrypto.Open(wrongKey, FromHex(FrameHex)));
    }

    [Fact]
    public void Base64Url_RoundTrips_NoPadding()
    {
        var bytes = FromHex(RelayRootHex);
        var encoded = RelayCrypto.Base64UrlNoPad(bytes);
        Assert.DoesNotContain('=', encoded);
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.Equal(bytes, RelayCrypto.FromBase64UrlNoPad(encoded));
    }

    [Fact]
    public void RidIsDerivableFromRoot_DerivedFromToken_EndToEnd()
    {
        // Token → relayRoot → rid, all the way through, matches the pinned rid.
        var root = RelayCrypto.DeriveRelayRoot(Token);
        Assert.Equal(Rid, RelayCrypto.DeriveRid(root));
    }

    // ── helpers ─────────────────────────────────────────────────────────────
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
