using System.Text.Json.Serialization;

namespace Nexus.Service.Rtc;

/// <summary>
/// POST /rtc/offer request body. The phone (offerer) creates the "runtime" and
/// "http" data channels, generates a connSalt per channel the same way a relay
/// connection does, and sends its offer SDP for the host to answer non-trickle.
/// </summary>
public sealed class RtcOfferRequest
{
    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = "";

    /// <summary>base64url-no-pad, 16 bytes; derives the "runtime" channel AEAD key.</summary>
    [JsonPropertyName("runtimeSalt")]
    public string RuntimeSalt { get; set; } = "";

    /// <summary>base64url-no-pad, 16 bytes; derives the "http" channel AEAD key.</summary>
    [JsonPropertyName("httpSalt")]
    public string HttpSalt { get; set; } = "";
}

/// <summary>POST /rtc/offer response: the host's non-trickle answer SDP.</summary>
public sealed class RtcAnswerResponse
{
    [JsonPropertyName("sdp")]
    public string Sdp { get; set; } = "";
}
