using System.Net;
using System.Text.Json;
using Qos.Service.Models.Panel;
using Qos.Service.Panel;
using Qos.Service.Persistence;
using Qos.Service.Serialization;
using Microsoft.AspNetCore.Http;

namespace Qos.Service.Tests;

public class PanelPhonePairingServiceTests
{
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";
    private const string AndroidUserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 Mobile Safari/537.36";
    private const string NativeIosUserAgent = "Qos/1 CFNetwork/3860.500.112 Darwin/25.4.0";

    [Fact]
    public void CreatePairQr_UsesSixtySecondTtl()
    {
        var service = NewService(new InMemoryConfigStore());

        var qr = service.CreatePairQr();

        Assert.Equal(60, qr.TtlSeconds);
    }

    [Fact]
    public void CreatePairQr_EmitsHttpPortForBrowserFallback()
    {
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Qos.Service.Sockets.MultiplexHub())
        {
            ServicePort = 9400,
            HttpsPort = 9443,
            SpkiFingerprint = "fp-stub",
            PublicLinkHost = "nexusqos.com",
        };

        var qr = service.CreatePairQr();

        Assert.Equal(9400, qr.HttpPort);
        Assert.Contains("httpPort=9400", qr.Url);
        Assert.Contains("port=9443", qr.Url);
    }

    [Fact]
    public void CreatePairQr_HttpPortTracksServicePortNotHttpsPort()
    {
        // Catches an alias regression where httpPort is accidentally derived
        // from HttpsPort instead of the plain-HTTP service port.
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Qos.Service.Sockets.MultiplexHub())
        {
            ServicePort = 9500,
            HttpsPort = 9443,
            SpkiFingerprint = "fp-stub",
            PublicLinkHost = "nexusqos.com",
        };

        var qr = service.CreatePairQr();

        Assert.Equal(9500, qr.HttpPort);
        Assert.Contains("httpPort=9500", qr.Url);
        Assert.DoesNotContain("httpPort=9443", qr.Url);
    }

    [Fact]
    public void CreatePairQr_EmbedsMachineNameForNativeAppLabel()
    {
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Qos.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "nexusqos.com",
        };
        service.SetHostDisplayName("Desk PC");

        var qr = service.CreatePairQr();

        Assert.Equal("Desk PC", qr.MachineName);
        Assert.Contains("machineName=Desk%20PC", qr.Url);
    }

    [Fact]
    public void Claim_ReturnsMachineNameForLegacyPairLinks()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SetHostDisplayName("Studio Workstation");

        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));

        Assert.True(result.Paired);
        Assert.Equal("Studio Workstation", result.MachineName);
    }

    [Fact]
    public void GetServiceInfo_ReturnsNormalizedMachineName()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SetHostDisplayName("  Studio   Mac  ");

        var result = service.GetServiceInfo();

        Assert.Equal("Studio Mac", result.MachineName);
    }

    [Fact]
    public void Claim_ReplacesExistingSessionForSameDeviceFingerprint()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));

        var sessions = service.GetSessions(0);

        Assert.True(first.Paired);
        Assert.True(second.Paired);
        Assert.Equal(1, sessions.AuthorizedCount);
        Assert.Single(sessions.Sessions);
        Assert.True(service.ValidateSessionToken(second.Token));
        Assert.False(service.ValidateSessionToken(first.Token));
    }

    [Fact]
    public void GetSessions_ReportsDeviceTypeFromUserAgent()
    {
        var service = NewService(new InMemoryConfigStore());
        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(AndroidUserAgent, "192.168.1.52"));

        var session = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(result.Paired);
        Assert.Equal("Android phone", session.Name);
        Assert.Equal("Android phone", session.DeviceType);
    }

    [Fact]
    public void GetSessions_ReportsIphoneForNativeIosUserAgent()
    {
        var service = NewService(new InMemoryConfigStore());
        var result = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));

        var session = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(result.Paired);
        Assert.Equal("iPhone", session.Name);
        Assert.Equal("iPhone", session.DeviceType);
    }

    [Fact]
    public void RenameSession_UpdatesAuthorizedDeviceName()
    {
        var service = NewService(new InMemoryConfigStore());
        service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(NativeIosUserAgent, "192.168.1.53"));
        var session = Assert.Single(service.GetSessions(0).Sessions);

        var renamed = service.RenameSession(session.Id, "Desk iPhone");
        var renamedSession = Assert.Single(service.GetSessions(0).Sessions);

        Assert.True(renamed);
        Assert.Equal("Desk iPhone", renamedSession.Name);
        Assert.Equal("iPhone", renamedSession.DeviceType);
    }

    [Fact]
    public void Claim_KeepsDifferentRemoteAddressesSeparate()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);

        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.51"));

        var sessions = service.GetSessions(0);

        Assert.True(first.Paired);
        Assert.True(second.Paired);
        Assert.Equal(2, sessions.AuthorizedCount);
        Assert.True(service.ValidateSessionToken(first.Token));
        Assert.True(service.ValidateSessionToken(second.Token));
    }

    [Fact]
    public void GetSessions_NormalizesExistingDuplicateFingerprints()
    {
        var store = new InMemoryConfigStore();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Update(s =>
        {
            s.Auth = new AuthSettings
            {
                PanelPhoneSessions =
                {
                    Session("old-duplicate", UserAgent, "192.168.1.50", now - 20_000),
                    Session("latest-duplicate", UserAgent, "192.168.1.50", now - 5_000),
                    Session("other-device", UserAgent, "192.168.1.51", now - 10_000),
                },
            };
        });
        var service = NewService(store);

        var sessions = service.GetSessions(0);
        var persisted = store.Load().Auth!.PanelPhoneSessions;

        Assert.Equal(2, sessions.AuthorizedCount);
        Assert.Contains(sessions.Sessions, session => session.Id == "latest-duplicate");
        Assert.DoesNotContain(sessions.Sessions, session => session.Id == "old-duplicate");
        Assert.Equal(2, persisted.Count);
        Assert.All(persisted, session => Assert.False(string.IsNullOrWhiteSpace(session.DeviceFingerprint)));
    }

    [Fact]
    public async Task RevokeAllSessions_RemovesEveryAuthorizedDevice()
    {
        var store = new InMemoryConfigStore();
        var service = NewService(store);
        var first = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));
        var second = service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.51"));

        var removed = await service.RevokeAllSessionsAsync();

        Assert.Equal(2, removed);
        Assert.Empty(service.GetSessions(0).Sessions);
        Assert.False(service.ValidateSessionToken(first.Token));
        Assert.False(service.ValidateSessionToken(second.Token));
    }

    [Fact]
    public async Task SetRemoteControlEnabled_FalseRefusesNewClaims()
    {
        var service = NewService(new InMemoryConfigStore());

        Assert.True(service.GetRemoteControlEnabled());
        await service.SetRemoteControlEnabledAsync(false);
        Assert.False(service.GetRemoteControlEnabled());

        var result = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(UserAgent, "192.168.1.60"));

        Assert.False(result.Paired);
        Assert.Equal("remote-disabled", result.Error);

        await service.SetRemoteControlEnabledAsync(true);
        var allowed = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(UserAgent, "192.168.1.60"));
        Assert.True(allowed.Paired);
    }

    [Fact]
    public void TryValidateSessionToken_ReturnsMatchedSessionId()
    {
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(NativeIosUserAgent, "192.168.1.53"));

        Assert.True(service.TryValidateSessionToken(claim.Token, context: null, out var sessionId));
        Assert.False(string.IsNullOrWhiteSpace(sessionId));

        var listed = Assert.Single(service.GetSessions(0).Sessions);
        Assert.Equal(listed.Id, sessionId);
    }

    [Fact]
    public void GetSessions_SerializesAuthorizedDevicesWithAppJsonContext()
    {
        var service = NewService(new InMemoryConfigStore());
        service.Claim(PairTokenFrom(service.CreatePairQr()), NewContext(UserAgent, "192.168.1.50"));

        var json = JsonSerializer.Serialize(
            service.GetSessions(1),
            AppJsonContext.Default.PanelPhoneSessionsResponse);

        Assert.Contains("\"connectedCount\":1", json);
        Assert.Contains("\"authorizedCount\":1", json);
        Assert.Contains("\"sessions\":[{", json);
        Assert.Contains("\"name\":\"iPhone\"", json);
        Assert.Contains("\"deviceType\":\"iPhone\"", json);
    }

    [Fact]
    public void Validate_HttpClaimedSession_RejectsRequestFromDifferentIp()
    {
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(UserAgent, "192.168.1.50", isHttps: false));
        Assert.True(claim.Paired);

        // Same device: passes
        Assert.True(service.ValidateSessionToken(claim.Token, NewContext(UserAgent, "192.168.1.50", isHttps: false)));

        // Different IP: rejected
        Assert.False(service.ValidateSessionToken(claim.Token, NewContext(UserAgent, "192.168.1.99", isHttps: false)));

        // Different UA: rejected
        Assert.False(service.ValidateSessionToken(claim.Token, NewContext(AndroidUserAgent, "192.168.1.50", isHttps: false)));
    }

    [Fact]
    public void Validate_HttpsClaimedSession_AcceptsRequestFromAnyIp()
    {
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(NativeIosUserAgent, "192.168.1.50", isHttps: true));
        Assert.True(claim.Paired);

        // Different IP from a different LAN run (DHCP renewal, Wi-Fi roam) -
        // legacy 30-day idle behavior must keep working for the native app.
        Assert.True(service.ValidateSessionToken(claim.Token, NewContext(NativeIosUserAgent, "192.168.1.99", isHttps: true)));
        Assert.True(service.ValidateSessionToken(claim.Token));
    }

    [Fact]
    public void Validate_NullContext_StillSucceedsForHttpsClaim()
    {
        // Internal callers (presence WS, contract tests) sometimes validate
        // without an HttpContext. Don't break them - the bind check only
        // runs when context is provided AND the session was HTTP-claimed.
        var service = NewService(new InMemoryConfigStore());
        var claim = service.Claim(
            PairTokenFrom(service.CreatePairQr()),
            NewContext(NativeIosUserAgent, "192.168.1.50", isHttps: true));

        Assert.True(service.ValidateSessionToken(claim.Token, context: null));
    }

    private static PanelPhonePairingService NewService(InMemoryConfigStore store)
    {
        return new PanelPhonePairingService(store, new Qos.Service.Sockets.MultiplexHub())
        {
            PublicLinkHost = "",
        };
    }

    private static DefaultHttpContext NewContext(string userAgent, string remoteAddress, bool isHttps = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = userAgent;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = isHttps ? "https" : "http";
        return context;
    }

    private static PanelPhoneSessionToken Session(string id, string userAgent, string remoteAddress, long lastSeenAt)
    {
        return new PanelPhoneSessionToken
        {
            Id = id,
            Hash = id,
            Name = "iPhone",
            UserAgent = userAgent,
            RemoteAddress = remoteAddress,
            CreatedAt = lastSeenAt - 1_000,
            LastSeenAt = lastSeenAt,
        };
    }

    private static string PairTokenFrom(PanelPhonePairQrResponse qr)
    {
        var query = new Uri(qr.Url).Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && pieces[0] == "pair")
                return Uri.UnescapeDataString(pieces[1]);
        }

        throw new InvalidOperationException("Pair QR did not contain a pair token.");
    }
}
