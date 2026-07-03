using Nexus.Service.Relay;
using Nexus.Service.Rtc;

namespace Nexus.Service.Tests.Rtc;

/// <summary>
/// Boundary tests for the oversized-response guard in RtcHttpChannel: a
/// RelayHttpResponse whose sealed frame would exceed the data channel's
/// 262144-byte max message size must be replaced with a small 413 reject
/// before it ever reaches RTCDataChannel.send (which throws past that size).
/// </summary>
public sealed class RtcHttpChannelFrameCapTests
{
    [Fact]
    public void ExceedsFrameCap_AtExactCapOnceSealed_ReturnsFalse()
    {
        var jsonLength = RtcHttpChannel.MaxFrameBytes - RtcHttpChannel.SealOverheadBytes;

        Assert.False(RtcHttpChannel.ExceedsFrameCap(jsonLength));
    }

    [Fact]
    public void ExceedsFrameCap_OneByteOverCapOnceSealed_ReturnsTrue()
    {
        var jsonLength = RtcHttpChannel.MaxFrameBytes - RtcHttpChannel.SealOverheadBytes + 1;

        Assert.True(RtcHttpChannel.ExceedsFrameCap(jsonLength));
    }

    [Fact]
    public void RejectOversized_CarriesMatchingIdAnd413Body()
    {
        var response = RtcHttpChannel.RejectOversized(42);

        Assert.Equal(42, response.Id);
        Assert.Equal(413, response.Status);
        Assert.Contains("response exceeds direct-channel cap", response.Body);
        Assert.Equal("application/json", response.ContentType);
    }

    [Fact]
    public void RejectOversized_SealsWithinTheFrameCap()
    {
        // The whole point of the substitution: sealing the ORIGINAL oversized
        // response would exceed RTCDataChannel's max message size and throw;
        // the substituted 413 must always fit, so RTCDataChannel.send never sees it.
        var response = RtcHttpChannel.RejectOversized(7);
        var key = RelayCrypto.DeriveAeadKey(RelayCrypto.DeriveRelayRoot("frame-cap-test-token"), new byte[RelayCrypto.ConnSaltLength]);
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            response, Nexus.Service.Serialization.AppJsonContext.Default.RelayHttpResponse);

        var frame = RelayCrypto.Seal(key, RelayCrypto.DirHostToClient, counter: 0, json);

        Assert.True(frame.Length <= RtcHttpChannel.MaxFrameBytes);
    }
}
