using System.Net;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Tests;

public class PanelPhonePairingServiceTests
{
    private const string UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";
    private const string AndroidUserAgent = "Mozilla/5.0 (Linux; Android 15; Pixel 9 Pro) AppleWebKit/537.36 Mobile Safari/537.36";
    private const string NativeIosUserAgent = "Nexus/1 CFNetwork/3860.500.112 Darwin/25.4.0";

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
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Nexus.Service.Sockets.MultiplexHub())
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
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Nexus.Service.Sockets.MultiplexHub())
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
        var service = new PanelPhonePairingService(new InMemoryConfigStore(), new Nexus.Service.Sockets.MultiplexHub())
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

    // -- Manual pair-code (BT-SSP Numeric Comparison) tests --------------

    [Fact]
    public void StartPairCode_ReturnsSixDigitCodeAndHostPort()
    {
        var service = NewService(new InMemoryConfigStore());
        service.ServicePort = 9400;

        var resp = service.StartPairCode();

        Assert.Equal(6, resp.Code.Length);
        Assert.All(resp.Code, c => Assert.InRange(c, '0', '9'));
        Assert.Equal(PanelPhonePairingService.PairCodeTtlSeconds, resp.TtlSeconds);
        Assert.True(resp.Port > 0);
        Assert.True(resp.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void SubmitPairCode_OverPlainHttp_Rejected()
    {
        var service = NewService(new InMemoryConfigStore());
        service.StartPairCode();
        var r = service.SubmitPairCode("123456", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: false));
        Assert.Equal("https-required", r.Error);
    }

    [Fact]
    public void ConfirmPairCode_OverPlainHttp_Rejected()
    {
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        var r = service.ConfirmPairCode(submit.RequestId, true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: false));
        Assert.Equal("https-required", r.Status);
    }

    [Fact]
    public void StartPairCode_RemoteDisabled_ReturnsEmpty()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SetRemoteControlEnabledAsync(false).GetAwaiter().GetResult();
        var resp = service.StartPairCode();
        Assert.Equal("", resp.Code);
        Assert.Equal(0, resp.TtlSeconds);
    }

    [Fact]
    public void SubmitPairCode_WrongCode_BurnsAttemptAndLocksOutAtFive()
    {
        var service = NewService(new InMemoryConfigStore());
        service.StartPairCode();

        for (var i = 0; i < 5; i++)
        {
            var r = service.SubmitPairCode("000000", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
            Assert.Equal("invalid-code", r.Error);
        }

        var locked = service.SubmitPairCode("000000", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("rate-limited", locked.Error);
        Assert.True(locked.RetryAfterSeconds > 0);
    }

    [Fact]
    public void SubmitPairCode_RightCode_ReturnsDeterministicSasAndRequestId()
    {
        var service = NewService(new InMemoryConfigStore());
        service.SpkiFingerprint = "fp-stub";
        var start = service.StartPairCode();

        var r = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        Assert.True(r.Accepted);
        Assert.Equal(6, r.Sas.Length);
        Assert.All(r.Sas, c => Assert.InRange(c, '0', '9'));
        Assert.False(string.IsNullOrEmpty(r.RequestId));
        Assert.Equal("fp-stub", r.SpkiFingerprint);
    }

    [Fact]
    public void SubmitPairCode_RightCode_AfterPriorFailures_ClearsLockoutCounter()
    {
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();

        for (var i = 0; i < 4; i++)
            service.SubmitPairCode("000000", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        var ok = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(ok.Accepted);

        // After success the counter is cleared; a fresh /start lets the
        // same IP retry without inheriting the prior session's strikes.
        var start2 = service.StartPairCode();
        for (var i = 0; i < 4; i++)
        {
            var r = service.SubmitPairCode("999999", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
            Assert.Equal("invalid-code", r.Error);
        }
        var ok2 = service.SubmitPairCode(start2.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(ok2.Accepted);
    }

    [Fact]
    public void SubmitPairCode_NoActiveCode_Rejected()
    {
        var service = NewService(new InMemoryConfigStore());
        var r = service.SubmitPairCode("123456", NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("no-active-code", r.Error);
    }

    [Fact]
    public void SubmitPairCode_SecondSubmit_WhileFirstInFlight_Rejected()
    {
        // Prevents the race where a second submitter (or attacker who got
        // the same plaintext code from somewhere) overwrites the in-flight
        // RequestId / PhoneRemoteAddress and hijacks the dual-confirm.
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var first = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(first.Accepted);

        var second = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.99", isHttps: true));
        Assert.Equal("code-in-use", second.Error);
    }

    [Fact]
    public void ComputeSas_DependsOnSpki()
    {
        // The MITM defense: same code + same nonce + different SPKI must
        // produce a different SAS. If this ever returns equal, the SPKI
        // binding regressed and the SAS comparison stops being a defense.
        var nonce = new byte[16];
        for (var i = 0; i < nonce.Length; i++) nonce[i] = (byte)i;

        var sasReal = PanelPhonePairingService.ComputeSas("123456", nonce, "real-spki");
        var sasMitm = PanelPhonePairingService.ComputeSas("123456", nonce, "attacker-spki");
        Assert.NotEqual(sasReal, sasMitm);

        // Deterministic for the same triple - the dashboard and the phone
        // both compute it from the same inputs and must agree.
        var sasRealAgain = PanelPhonePairingService.ComputeSas("123456", nonce, "real-spki");
        Assert.Equal(sasReal, sasRealAgain);

        // Shape: 6 digits.
        Assert.Equal(6, sasReal.Length);
        Assert.All(sasReal, c => Assert.InRange(c, '0', '9'));
    }

    [Fact]
    public void HostApprove_ThenPhonePoll_IssuesToken()
    {
        // The phone's polling /confirm IS the wait. There's no separate
        // "phone approves" gesture - the user-visible model is "type code,
        // compare SAS, click Allow on the system, phone connects."
        var store = new InMemoryConfigStore();
        var service = NewService(store);
        service.SpkiFingerprint = "fp-stub";
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.True(submit.Accepted);

        // Phone polls before host approves -> waiting-host, no token yet.
        var beforeApprove = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("waiting-host", beforeApprove.Status);
        Assert.Equal("", beforeApprove.Token);

        var host = service.HostDecisionPairCode(submit.RequestId, approved: true);
        Assert.Equal("approved", host.Status);

        // Next poll picks up the host approval and gets the token.
        var afterApprove = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("approved", afterApprove.Status);
        Assert.False(string.IsNullOrEmpty(afterApprove.Token));
        Assert.Equal("fp-stub", afterApprove.SpkiFingerprint);

        Assert.True(service.ValidateSessionToken(afterApprove.Token, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true)));
    }

    [Fact]
    public void HostDeny_PhoneSeesDeniedOnNextPoll()
    {
        // Previously: state was nulled on host-deny, so the phone's next
        // poll got "unknown" - indistinguishable from a stale requestId.
        // Now state is kept (HostDenied=true) and the phone learns the
        // canonical "denied".
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        var host = service.HostDecisionPairCode(submit.RequestId, approved: false);
        Assert.Equal("denied", host.Status);

        var confirm = service.ConfirmPairCode(submit.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("denied", confirm.Status);
    }

    [Fact]
    public void PhoneDeny_Cancels_HostSeesUnknownAfter()
    {
        var service = NewService(new InMemoryConfigStore());
        var start = service.StartPairCode();
        var submit = service.SubmitPairCode(start.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        var phoneDeny = service.ConfirmPairCode(submit.RequestId, approved: false, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("denied", phoneDeny.Status);

        var host = service.HostDecisionPairCode(submit.RequestId, approved: true);
        Assert.Equal("unknown", host.Status);
    }

    [Fact]
    public void Start_AfterInflightSubmit_SupersedesPriorRequest()
    {
        var service = NewService(new InMemoryConfigStore());
        var start1 = service.StartPairCode();
        var submit1 = service.SubmitPairCode(start1.Code, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));

        service.StartPairCode();
        var followUp = service.ConfirmPairCode(submit1.RequestId, approved: true, NewContext(NativeIosUserAgent, "192.168.1.77", isHttps: true));
        Assert.Equal("unknown", followUp.Status);
    }

    private static PanelPhonePairingService NewService(InMemoryConfigStore store)
    {
        return new PanelPhonePairingService(store, new Nexus.Service.Sockets.MultiplexHub())
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
