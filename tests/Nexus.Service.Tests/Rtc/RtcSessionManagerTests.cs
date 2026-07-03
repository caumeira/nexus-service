using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Rtc;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using SIPSorcery.Net;

namespace Nexus.Service.Tests.Rtc;

/// <summary>
/// In-proc loopback integration for the WebRTC DataChannel direct P2P host
/// side. A "phone" RTCPeerConnection (empty iceServers - host candidates
/// suffice for loopback) offers, RtcSessionManager.HandleOfferAsync answers
/// with the real STUN-configured host peer connection, and the two connect
/// over real UDP/DTLS/SCTP on 127.0.0.1.
/// </summary>
public sealed class RtcSessionManagerTests
{
    private const string SessionId = "sess-rtc";
    private const string Topic = "rtc-roundtrip-topic";
    private static readonly TimeSpan ChannelOpenTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Offer_RemoteControlDisabled_Rejected()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-disabled-token");
        var store = StoreWithSession(relayRoot, remoteOn: false, relayOn: true);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var result = await rtc.HandleOfferAsync(SessionId, new RtcOfferRequest(), CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Offer_RelayDisabled_Rejected()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-relay-off-token");
        var store = StoreWithSession(relayRoot, remoteOn: true, relayOn: false);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var result = await rtc.HandleOfferAsync(SessionId, new RtcOfferRequest(), CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Offer_UnknownSession_Rejected()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = true;
            s.Auth.RelayEnabled = true;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>();
        });
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var result = await rtc.HandleOfferAsync("no-such-session", new RtcOfferRequest(), CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Offer_MalformedSalt_Rejected()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-bad-salt-token");
        var store = StoreWithSession(relayRoot, remoteOn: true, relayOn: true);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var request = new RtcOfferRequest
        {
            Sdp = "v=0",
            RuntimeSalt = "not-valid-base64url!!",
            HttpSalt = RelayCrypto.Base64UrlNoPad(RandomBytes(16)),
        };
        var result = await rtc.HandleOfferAsync(SessionId, request, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Offer_WrongLengthSalt_Rejected()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-short-salt-token");
        var store = StoreWithSession(relayRoot, remoteOn: true, relayOn: true);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var request = new RtcOfferRequest
        {
            Sdp = "v=0",
            RuntimeSalt = RelayCrypto.Base64UrlNoPad(RandomBytes(8)), // wrong length
            HttpSalt = RelayCrypto.Base64UrlNoPad(RandomBytes(16)),
        };
        var result = await rtc.HandleOfferAsync(SessionId, request, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task Offer_Success_RuntimeAndHttpChannelsRoundTrip()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-roundtrip-token");
        var store = StoreWithSession(relayRoot, remoteOn: true, relayOn: true);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var phone = await ConnectPhoneAsync(rtc, SessionId, relayRoot);
        try
        {
            // Runtime: a sealed `sub` command reaches the hub, and a hub
            // broadcast on that topic round-trips back sealed (dir=1, counter 0).
            var subCommand = Encoding.UTF8.GetBytes($"{{\"sub\":[\"{Topic}\"]}}");
            var subFrame = RelayCrypto.Seal(phone.RuntimeKey, RelayCrypto.DirClientToHost, counter: 0, subCommand);
            phone.RuntimeChannel.send(subFrame);

            await WaitUntilAsync(() => hub.TopicHasSubscribers(Topic), TimeSpan.FromSeconds(10),
                "sealed sub command over the runtime data channel never reached the hub");

            var runtimeReply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            phone.RuntimeChannel.onmessage += (_, proto, data) =>
            {
                if (proto == DataChannelPayloadProtocols.WebRTC_Binary)
                    runtimeReply.TrySetResult(data);
            };

            var payload = Encoding.UTF8.GetBytes($"{{\"t\":\"{Topic}\",\"d\":42}}");
            await hub.BroadcastTopicAsync(Topic, payload);

            var hostFrame = await runtimeReply.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var (dir, counter, plaintext) = RelayCrypto.Open(phone.RuntimeKey, hostFrame);
            Assert.Equal(RelayCrypto.DirHostToClient, dir);
            Assert.Equal(0ul, counter);
            Assert.Equal(payload, plaintext);

            // Http: a sealed RelayHttpRequest gets a sealed RelayHttpResponse back.
            // The dispatcher is inert (never primed), so its 503 "not ready" reply
            // is what proves the round trip; the dispatch path itself is already
            // covered end-to-end by RelayHttpDispatcherTests.
            var httpReply = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            phone.HttpChannel.onmessage += (_, proto, data) =>
            {
                if (proto == DataChannelPayloadProtocols.WebRTC_Binary)
                    httpReply.TrySetResult(data);
            };

            var httpRequest = new RelayHttpRequest { Id = 7, Method = "GET", Path = "/ping" };
            var reqJson = JsonSerializer.SerializeToUtf8Bytes(httpRequest, AppJsonContext.Default.RelayHttpRequest);
            var reqFrame = RelayCrypto.Seal(phone.HttpKey, RelayCrypto.DirClientToHost, counter: 0, reqJson);
            phone.HttpChannel.send(reqFrame);

            var httpFrame = await httpReply.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var (httpDir, httpCounter, httpPlaintext) = RelayCrypto.Open(phone.HttpKey, httpFrame);
            Assert.Equal(RelayCrypto.DirHostToClient, httpDir);
            Assert.Equal(0ul, httpCounter);
            var response = JsonSerializer.Deserialize(httpPlaintext, AppJsonContext.Default.RelayHttpResponse);
            Assert.NotNull(response);
            Assert.Equal(7, response!.Id);
            Assert.Equal(503, response.Status);
        }
        finally
        {
            phone.Pc.Close("test done");
            rtc.CloseAll();
        }
    }

    [Fact]
    public async Task SecondOffer_DisposesFirstPeerConnection()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-second-offer-token");
        var store = StoreWithSession(relayRoot, remoteOn: true, relayOn: true);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var first = await ConnectPhoneAsync(rtc, SessionId, relayRoot);
        var firstClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.RuntimeChannel.onclose += () => firstClosed.TrySetResult(true);

        RtcPhone second;
        try
        {
            second = await ConnectPhoneAsync(rtc, SessionId, relayRoot);
        }
        finally
        {
            first.Pc.Close("test done");
        }

        try
        {
            await firstClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            second.Pc.Close("test done");
            rtc.CloseAll();
        }
    }

    [Fact]
    public async Task KickPhoneSessions_ClosesTheDirectChannel()
    {
        var relayRoot = RelayCrypto.DeriveRelayRoot("rtc-kick-token");
        var store = StoreWithSession(relayRoot, remoteOn: true, relayOn: true);
        var hub = new MultiplexHub();
        var pairing = new Nexus.Service.Panel.PanelPhonePairingService(store, hub) { PublicLinkHost = "" };
        var rtc = new RtcSessionManager(
            NullLogger<RtcSessionManager>.Instance, NullLoggerFactory.Instance,
            pairing, store, hub, RelayTestHelpers.InertHttpDispatcher());

        var phone = await ConnectPhoneAsync(rtc, SessionId, relayRoot);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        phone.RuntimeChannel.onclose += () => closed.TrySetResult(true);

        try
        {
            await hub.KickPhoneSessionsAsync(new[] { SessionId });
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            phone.Pc.Close("test done");
            rtc.CloseAll();
        }
    }

    private readonly record struct RtcPhone(
        RTCPeerConnection Pc, RTCDataChannel RuntimeChannel, RTCDataChannel HttpChannel, byte[] RuntimeKey, byte[] HttpKey);

    private static async Task<RtcPhone> ConnectPhoneAsync(RtcSessionManager rtc, string sessionId, byte[] relayRoot)
    {
        // Empty iceServers: host (loopback) candidates are enough for an
        // in-proc connection, and gathering completes without a STUN round trip.
        var phonePc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });
        var runtimeChannel = await phonePc.createDataChannel("runtime", null);
        var httpChannel = await phonePc.createDataChannel("http", null);

        var offerInit = phonePc.createOffer(null);
        await phonePc.setLocalDescription(offerInit);

        var runtimeSalt = RandomBytes(16);
        var httpSalt = RandomBytes(16);
        var request = new RtcOfferRequest
        {
            Sdp = offerInit.sdp,
            RuntimeSalt = RelayCrypto.Base64UrlNoPad(runtimeSalt),
            HttpSalt = RelayCrypto.Base64UrlNoPad(httpSalt),
        };

        var result = await rtc.HandleOfferAsync(sessionId, request, CancellationToken.None);
        Assert.True(result.Ok, result.Error);

        var answerInit = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = result.Sdp };
        var setResult = phonePc.setRemoteDescription(answerInit);
        Assert.Equal(SetDescriptionResultEnum.OK, setResult);

        await WaitUntilAsync(() => runtimeChannel.readyState == RTCDataChannelState.open, ChannelOpenTimeout,
            "runtime data channel never opened");
        await WaitUntilAsync(() => httpChannel.readyState == RTCDataChannelState.open, ChannelOpenTimeout,
            "http data channel never opened");

        var runtimeKey = RelayCrypto.DeriveAeadKey(relayRoot, runtimeSalt);
        var httpKey = RelayCrypto.DeriveAeadKey(relayRoot, httpSalt);
        return new RtcPhone(phonePc, runtimeChannel, httpChannel, runtimeKey, httpKey);
    }

    private static InMemoryConfigStore StoreWithSession(byte[] relayRoot, bool remoteOn, bool relayOn)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.RemoteControlEnabled = remoteOn;
            s.Auth.RelayEnabled = relayOn;
            s.Auth.PanelPhoneSessions = new List<PanelPhoneSessionToken>
            {
                new()
                {
                    Id = SessionId,
                    Hash = "hash-placeholder",
                    RelayKey = Convert.ToBase64String(relayRoot),
                    Name = "iPhone",
                    UserAgent = "iPhone",
                    RemoteAddress = "192.168.1.60",
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ClaimedOverHttps = true,
                },
            };
        });
        return store;
    }

    private static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failMessage)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
                return;
            await Task.Delay(20);
        }
        Assert.Fail(failMessage);
    }
}

/// <summary>In-memory IConfigStore for unit tests - no disk I/O.</summary>
internal sealed class InMemoryConfigStore : IConfigStore
{
    private NexusSettings _settings = new();

    public string SettingsPath => ":memory:";

    public NexusSettings Load() => _settings;

    public void Update(Action<NexusSettings> mutator)
    {
        mutator(_settings);
        OnChanged?.Invoke();
    }

    public void Reload() { _settings = new(); }

    public void FlushNow() { }

    public event Action? OnChanged;
}
