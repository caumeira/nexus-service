using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;
using Nexus.Service.Net;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

public sealed class PanelPhonePairingService
{
    public const string PresenceTopic = "panel/phone/presence";
    public const string SessionCookieName = "nexus_phone_token";
    public static readonly TimeSpan SessionIdle = TimeSpan.FromDays(30);
    /// <summary>
    /// Sessions claimed over plain HTTP (browser fallback) get a much
    /// shorter idle window than the SPKI-pinned HTTPS path, so a sniffed
    /// cookie expires in hours rather than weeks.
    /// </summary>
    public static readonly TimeSpan HttpSessionIdle = TimeSpan.FromHours(24);
    private const int PairTtlSeconds = 60;
    private const int MaxSessions = 12;
    private const long LastSeenRefreshMs = 60_000;
    private const long RecentSessionWindowMs = LastSeenRefreshMs * 2 + 15_000;
    private static readonly long SessionIdleMs = (long)SessionIdle.TotalMilliseconds;
    private static readonly long HttpSessionIdleMs = (long)HttpSessionIdle.TotalMilliseconds;

    // Manual pair-code (BT-SSP-style numeric comparison) tunables. Matches
    // the QR TTL so both pairing paths feel identical from the dashboard.
    public const int PairCodeTtlSeconds = 60;
    private const int PairCodeMaxAttempts = 5;
    private const long PairCodeLockoutMs = 5 * 60 * 1000;
    private static readonly byte[] SasInfoPrefix = Encoding.UTF8.GetBytes("nexus-pair-sas-v1|");

    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _pairTokens = new(StringComparer.Ordinal);

    // Pair-code state. _pairCode holds the one active code (replaced on
    // /start); _pairCodeAttempts tracks per-IP brute-force lockouts.
    private readonly object _pairCodeLock = new();
    private PairCodeState? _pairCode;
    private readonly Dictionary<string, PairCodeAttempt> _pairCodeAttempts = new(StringComparer.Ordinal);

    public int ServicePort { get; set; } = 9400;
    public int HttpsPort { get; set; }

    /// <summary>
    /// SHA-256(SPKI) of the local HTTPS cert, base64url-no-pad. Embedded in
    /// the pair QR's Universal Link as the `fp` query param so the iOS
    /// companion app can cert-pin before its first TLS handshake.
    /// </summary>
    public string SpkiFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Public Universal Link host. The QR encodes
    /// `https://&lt;PublicLinkHost&gt;/r/pair?host=&lt;lan&gt;&port=&lt;p&gt;&pair=&lt;t&gt;&fp=&lt;spki&gt;`
    /// so iOS Camera + Universal Links + the app's in-app scanner all decode
    /// the same payload. The web fallback at /r/pair handles "no app installed"
    /// by redirecting to the LAN URL or linking to the App Store.
    /// </summary>
    public string PublicLinkHost { get; set; } = "hellonexus.com";

    /// <summary>
    /// Resolves the user-visible host PC name. Reads
    /// NexusSettings.HostDisplayName each call so a settings update is
    /// reflected immediately in the next QR / claim / /ping payload, then
    /// falls back to the OS-reported machine name when the override is empty.
    /// </summary>
    public string MachineName => ResolveMachineName();

    public PanelPhonePairingService(IConfigStore store, MultiplexHub hub)
    {
        _store = store;
        _hub = hub;
    }

    private string ResolveMachineName()
    {
        var settings = _store.Load();
        var overrideName = settings.HostDisplayName;
        if (!string.IsNullOrWhiteSpace(overrideName))
            return overrideName;
        return GetDefaultMachineName();
    }

    /// <summary>
    /// Persists the user-overridden host display name. Whitespace clears
    /// the override so subsequent reads fall back to the OS machine name.
    /// Input is trimmed, internal whitespace runs collapsed, and length
    /// capped at 64 chars (matching the QR/claim normalisation) so a
    /// LAN client can't bloat settings.json with arbitrary bytes.
    /// </summary>
    public string SetHostDisplayName(string? value)
    {
        string normalized;
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = "";
        }
        else
        {
            var collapsed = string.Join(
                " ",
                value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            normalized = collapsed.Length <= 64 ? collapsed : collapsed[..64];
        }
        _store.Update(s =>
        {
            s.HostDisplayName = normalized;
        });
        return ResolveMachineName();
    }

    public PanelPhonePairQrResponse CreatePairQr()
    {
        PruneExpiredPairs();
        var token = CreateToken(24);
        var expires = DateTime.UtcNow.AddSeconds(PairTtlSeconds);
        lock (_lock)
        {
            _pairTokens[token] = expires;
        }

        var lanHost = LocalNetwork.GetLocalIp();
        var lanPort = HttpsPort > 0 ? HttpsPort : ServicePort;
        var machineName = NormalizeMachineName(MachineName);
        var url = BuildPairUrl(lanHost, lanPort, token, machineName);
        return new PanelPhonePairQrResponse
        {
            Url = url,
            QrDataUrl = LocalNetwork.GenerateQrSvgDataUrl(url),
            MachineName = machineName,
            TtlSeconds = PairTtlSeconds,
            ExpiresAt = new DateTimeOffset(expires).ToUnixTimeMilliseconds(),
            HttpPort = ServicePort,
        };
    }

    private string BuildPairUrl(string lanHost, int lanPort, string pairToken, string machineName)
    {
        // When the public link host is configured, emit the Universal Link
        // form so iOS hands the URL off to the installed app. When unset
        // (testing), fall back to the legacy direct LAN URL so phones on
        // the same network can still open the panel in Safari.
        if (string.IsNullOrWhiteSpace(PublicLinkHost))
        {
            var scheme = HttpsPort > 0 ? "https" : "http";
            return $"{scheme}://{lanHost}:{lanPort}/panel/phone?pair={Uri.EscapeDataString(pairToken)}&machineName={Uri.EscapeDataString(machineName)}";
        }

        var qs = new StringBuilder();
        qs.Append("host=").Append(Uri.EscapeDataString(lanHost));
        qs.Append("&port=").Append(lanPort);
        qs.Append("&pair=").Append(Uri.EscapeDataString(pairToken));
        qs.Append("&machineName=").Append(Uri.EscapeDataString(machineName));
        if (!string.IsNullOrEmpty(SpkiFingerprint))
        {
            qs.Append("&fp=").Append(Uri.EscapeDataString(SpkiFingerprint));
        }
        // Plain-HTTP port for the browser fallback at /r/pair (Continue in
        // browser). Native iOS uses `port` (HTTPS) + SPKI pinning instead.
        qs.Append("&httpPort=").Append(ServicePort);
        return $"https://{PublicLinkHost}/r/pair?{qs}";
    }

    public PanelPhoneClaimResponse Claim(string pairToken, HttpContext context)
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhoneClaimResponse { Paired = false, Error = "remote-disabled" };

        if (string.IsNullOrWhiteSpace(pairToken))
            return new PanelPhoneClaimResponse { Paired = false, Error = "missing pairing token" };

        lock (_lock)
        {
            PruneExpiredPairsLocked();
            if (!_pairTokens.TryGetValue(pairToken, out var expires) || expires <= DateTime.UtcNow)
            {
                _pairTokens.Remove(pairToken);
                return new PanelPhoneClaimResponse { Paired = false, Error = "pairing token expired" };
            }
            _pairTokens.Remove(pairToken);
        }

        var sessionToken = CreateToken(32);
        var hash = HashToken(sessionToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "";
        var deviceFingerprint = BuildDeviceFingerprint(userAgent, remoteAddress);
        var claimedOverHttps = context.Request.IsHttps;

        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            if (!string.IsNullOrEmpty(deviceFingerprint))
            {
                s.Auth.PanelPhoneSessions.RemoveAll(session =>
                    string.Equals(GetSessionFingerprint(session), deviceFingerprint, StringComparison.Ordinal));
            }
            s.Auth.PanelPhoneSessions.Insert(0, new PanelPhoneSessionToken
            {
                Id = CreateToken(9),
                Hash = hash,
                Name = DescribeDevice(userAgent),
                UserAgent = userAgent,
                RemoteAddress = remoteAddress,
                DeviceFingerprint = deviceFingerprint,
                CreatedAt = now,
                LastSeenAt = now,
                ClaimedOverHttps = claimedOverHttps,
            });
            NormalizeSessionList(s.Auth.PanelPhoneSessions, now);
        });

        return new PanelPhoneClaimResponse
        {
            Paired = true,
            Token = sessionToken,
            MachineName = NormalizeMachineName(MachineName),
        };
    }

    public PanelPhoneServiceInfoResponse GetServiceInfo()
        => new() { MachineName = NormalizeMachineName(MachineName) };

    public PanelPhoneSessionsResponse GetSessions(int connectedCount)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = GetNormalizedSessionSnapshot(now);
        var items = sessions
            .OrderByDescending(GetSessionActivity)
            .Select(s =>
            {
                var lastSeen = GetSessionActivity(s);
                var deviceType = DescribeDevice(s.UserAgent);
                var displayName = string.IsNullOrWhiteSpace(s.Name) ||
                    (string.Equals(s.Name, "Phone remote", StringComparison.Ordinal) && deviceType != "Phone remote")
                    ? deviceType
                    : s.Name;
                return new PanelPhoneSessionDto
                {
                    Id = s.Id,
                    Name = displayName,
                    DeviceType = deviceType,
                    UserAgent = s.UserAgent,
                    RemoteAddress = s.RemoteAddress,
                    CreatedAt = s.CreatedAt,
                    LastSeenAt = lastSeen,
                    ExpiresAt = lastSeen > 0 ? lastSeen + SessionIdleMs : 0,
                    RecentlyActive = lastSeen > 0 && now - lastSeen <= RecentSessionWindowMs,
                };
            })
            .ToList();

        return new PanelPhoneSessionsResponse
        {
            ConnectedCount = connectedCount,
            AuthorizedCount = items.Count,
            SessionIdleMs = SessionIdleMs,
            Now = now,
            Sessions = items,
        };
    }

    public async Task<bool> RevokeSessionAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var removed = false;
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;
            removed = sessions.RemoveAll(session =>
                string.Equals(session.Id, id, StringComparison.Ordinal)) > 0;
        });
        if (removed)
            await _hub.KickPhoneSessionsAsync(new[] { id });
        return removed;
    }

    public bool RenameSession(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return false;

        var nextName = name.Trim();
        if (nextName.Length > 40)
            nextName = nextName[..40];

        var updated = false;
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;

            foreach (var session in sessions)
            {
                if (!string.Equals(session.Id, id, StringComparison.Ordinal))
                    continue;

                session.Name = nextName;
                updated = true;
                break;
            }
        });
        return updated;
    }

    public async Task<int> RevokeAllSessionsAsync()
    {
        var removed = 0;
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;

            removed = sessions.Count;
            sessions.Clear();
        });
        if (removed > 0)
            await _hub.KickAllPhoneAsync();
        return removed;
    }

    /// <summary>
    /// Returns true if Pair Remote requests are accepted. When false, the
    /// auth middleware rejects any phone-session-authenticated request with
    /// 403 RemoteDisabled and the claim endpoint refuses new pairings.
    /// </summary>
    public bool GetRemoteControlEnabled()
    {
        var auth = _store.Load().Auth;
        return auth?.RemoteControlEnabled ?? true;
    }

    /// <summary>
    /// Persists the killswitch state. On a true -> false transition every
    /// active phone-session WebSocket is closed immediately so the user's
    /// "OFF means OFF" expectation holds without waiting for cookie expiry
    /// or the next HTTP request.
    /// </summary>
    public async Task SetRemoteControlEnabledAsync(bool enabled)
    {
        var changed = false;
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            if (s.Auth.RemoteControlEnabled == enabled)
                return;
            s.Auth.RemoteControlEnabled = enabled;
            changed = true;
        });
        if (changed && !enabled)
            await _hub.KickAllPhoneAsync();
    }

    /// <summary>
    /// Returns the current Wi-Fi discoverability preference. Reading
    /// passes through the persisted document and collapses an expired
    /// "until <ts>" window to "never" so callers see a single source of
    /// truth without having to check the timestamp themselves. Does not
    /// mutate the persisted state.
    /// </summary>
    public (string Mode, long UntilUnixSeconds) GetPairBroadcast()
    {
        var raw = _store.Load().Auth?.PairBroadcast ?? new PairBroadcastSettings();
        if (raw.Mode == "until" && raw.UntilUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return ("never", 0);
        return (raw.Mode, raw.UntilUnixSeconds);
    }

    /// <summary>
    /// Persists a Wi-Fi discoverability preference. <paramref name="mode"/>
    /// must be "never", "always", or "until"; for "until" the caller passes
    /// a future Unix-seconds expiry (clamped to a maximum of 24h ahead so a
    /// stale write can't pin broadcast on forever).
    /// </summary>
    public void SetPairBroadcast(string mode, long untilUnixSeconds)
    {
        // Default unknown modes to "never" rather than "always": a misbehaving
        // client sending an unrecognised string should land in the safer state,
        // not silently enable LAN discoverability.
        var normalized = mode switch
        {
            "never" => ("never", 0L),
            "always" => ("always", 0L),
            "until" => ("until", Math.Min(
                untilUnixSeconds,
                DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds())),
            _ => ("never", 0L)
        };
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PairBroadcast ??= new PairBroadcastSettings();
            s.Auth.PairBroadcast.Mode = normalized.Item1;
            s.Auth.PairBroadcast.UntilUnixSeconds = normalized.Item2;
        });
    }

    public bool ValidateSessionToken(string? token)
        => TryValidateSessionToken(token, context: null, out _);

    public bool ValidateSessionToken(string? token, HttpContext? context)
        => TryValidateSessionToken(token, context, out _);

    /// <summary>
    /// Same as <see cref="ValidateSessionToken(string?, HttpContext?)"/> but
    /// returns the matched session id on success. The auth middleware uses
    /// this to stash the id on <see cref="HttpContext.Items"/> so the WS
    /// upgrade can tag the connection for the Pair Remote killswitch.
    /// </summary>
    public bool TryValidateSessionToken(string? token, HttpContext? context, out string sessionId)
    {
        sessionId = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var hash = HashToken(token);
        var provided = Encoding.UTF8.GetBytes(hash);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = _store.Load().Auth?.PanelPhoneSessions;
        if (sessions is null)
            return false;

        foreach (var session in sessions)
        {
            if (string.IsNullOrEmpty(session.Hash))
                continue;
            var expected = Encoding.UTF8.GetBytes(session.Hash);
            if (provided.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                var lastSeen = session.LastSeenAt > 0 ? session.LastSeenAt : session.CreatedAt;
                var idleLimit = session.ClaimedOverHttps ? SessionIdleMs : HttpSessionIdleMs;
                if (lastSeen <= 0 || now - lastSeen > idleLimit)
                {
                    PruneExpiredSessions(now);
                    return false;
                }

                // HTTP-claimed sessions get a hard IP+UA bind on every
                // request: a leaked cookie can't ride from a different
                // device. HTTPS-claimed sessions skip the check because
                // SPKI pinning already covers the threat and bind drift
                // (DHCP renewal, Wi-Fi roam, iOS UA bump) would 401 the
                // native app for no security gain.
                if (!session.ClaimedOverHttps && context is not null)
                {
                    var requestUserAgent = context.Request.Headers["User-Agent"].ToString();
                    var requestRemote = context.Connection.RemoteIpAddress?.ToString() ?? "";
                    var requestFingerprint = BuildDeviceFingerprint(requestUserAgent, requestRemote);
                    var sessionFingerprint = GetSessionFingerprint(session);
                    if (string.IsNullOrEmpty(requestFingerprint) ||
                        string.IsNullOrEmpty(sessionFingerprint) ||
                        !string.Equals(requestFingerprint, sessionFingerprint, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                if (now - lastSeen >= LastSeenRefreshMs)
                    TouchSession(hash, now);
                sessionId = session.Id ?? "";
                return true;
            }
        }

        return false;
    }

    private void PruneExpiredPairs()
    {
        lock (_lock)
        {
            PruneExpiredPairsLocked();
        }
    }

    private void PruneExpiredPairsLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var token in _pairTokens.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList())
            _pairTokens.Remove(token);
    }

    private void TouchSession(string hash, long now)
    {
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;
            foreach (var session in sessions)
            {
                if (session.Hash != hash)
                    continue;
                session.LastSeenAt = now;
                break;
            }
        });
    }

    private void PruneExpiredSessions(long now)
    {
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;
            sessions.RemoveAll(session =>
            {
                var lastSeen = GetSessionActivity(session);
                return lastSeen <= 0 || now - lastSeen > SessionIdleMs;
            });
        });
    }

    private List<PanelPhoneSessionToken> GetNormalizedSessionSnapshot(long now)
    {
        var sessions = _store.Load().Auth?.PanelPhoneSessions;
        if (sessions is null)
            return new List<PanelPhoneSessionToken>();

        var needsUpdate = NeedsSessionNormalization(sessions, now);

        if (!needsUpdate)
            return sessions.Select(CloneSession).ToList();

        var snapshot = new List<PanelPhoneSessionToken>();
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            NormalizeSessionList(s.Auth.PanelPhoneSessions, now);
            snapshot = s.Auth.PanelPhoneSessions.Select(CloneSession).ToList();
        });

        return snapshot;
    }

    private static bool ShouldPruneSession(PanelPhoneSessionToken session, long now)
    {
        var lastSeen = GetSessionActivity(session);
        return lastSeen <= 0 || now - lastSeen > SessionIdleMs;
    }

    private static bool NeedsSessionNormalization(List<PanelPhoneSessionToken> sessions, long now)
    {
        if (sessions.Count > MaxSessions)
            return true;

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            var fingerprint = GetSessionFingerprint(session);
            if (ShouldPruneSession(session, now) ||
                string.IsNullOrWhiteSpace(session.Id) ||
                session.CreatedAt <= 0 ||
                session.LastSeenAt <= 0 ||
                string.IsNullOrWhiteSpace(session.Name) ||
                (string.IsNullOrWhiteSpace(session.DeviceFingerprint) && !string.IsNullOrEmpty(fingerprint)))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(fingerprint) && !fingerprints.Add(fingerprint))
                return true;
        }

        return false;
    }

    private static void NormalizeSessionList(List<PanelPhoneSessionToken> sessions, long now)
    {
        sessions.RemoveAll(session => ShouldPruneSession(session, now));
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Id))
                session.Id = CreateToken(9);
            if (session.CreatedAt <= 0)
                session.CreatedAt = GetSessionActivity(session);
            if (session.CreatedAt <= 0)
                session.CreatedAt = now;
            if (session.LastSeenAt <= 0)
                session.LastSeenAt = session.CreatedAt;
            if (string.IsNullOrWhiteSpace(session.Name))
                session.Name = DescribeDevice(session.UserAgent);
            if (string.IsNullOrWhiteSpace(session.DeviceFingerprint))
                session.DeviceFingerprint = BuildDeviceFingerprint(session.UserAgent, session.RemoteAddress);
        }

        sessions.Sort((a, b) => GetSessionActivity(b).CompareTo(GetSessionActivity(a)));

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sessions.Count;)
        {
            var fingerprint = GetSessionFingerprint(sessions[i]);
            if (!string.IsNullOrEmpty(fingerprint) && !fingerprints.Add(fingerprint))
            {
                sessions.RemoveAt(i);
                continue;
            }
            i++;
        }

        if (sessions.Count > MaxSessions)
            sessions.RemoveRange(MaxSessions, sessions.Count - MaxSessions);
    }

    private static long GetSessionActivity(PanelPhoneSessionToken session)
    {
        return session.LastSeenAt > 0 ? session.LastSeenAt : session.CreatedAt;
    }

    private static string GetSessionFingerprint(PanelPhoneSessionToken session)
    {
        return string.IsNullOrWhiteSpace(session.DeviceFingerprint)
            ? BuildDeviceFingerprint(session.UserAgent, session.RemoteAddress)
            : session.DeviceFingerprint;
    }

    private static PanelPhoneSessionToken CloneSession(PanelPhoneSessionToken session)
    {
        return new PanelPhoneSessionToken
        {
            Id = session.Id,
            Hash = session.Hash,
            Name = session.Name,
            UserAgent = session.UserAgent,
            RemoteAddress = session.RemoteAddress,
            DeviceFingerprint = session.DeviceFingerprint,
            CreatedAt = session.CreatedAt,
            LastSeenAt = session.LastSeenAt,
            ClaimedOverHttps = session.ClaimedOverHttps,
        };
    }

    private static string DescribeDevice(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Phone remote";

        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
            return "iPad";
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
            return "iPhone";
        if (userAgent.Contains("Nexus/", StringComparison.OrdinalIgnoreCase) &&
            userAgent.Contains("CFNetwork", StringComparison.OrdinalIgnoreCase) &&
            userAgent.Contains("Darwin", StringComparison.OrdinalIgnoreCase))
        {
            return "iPhone";
        }
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
                ? "Android phone"
                : "Android tablet";
        }
        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
            return "Mac";
        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return "Windows PC";

        return "Phone remote";
    }

    private static string BuildDeviceFingerprint(string? userAgent, string? remoteAddress)
    {
        var normalizedUserAgent = NormalizeFingerprintPart(userAgent);
        var normalizedRemoteAddress = NormalizeRemoteAddress(remoteAddress);
        if (string.IsNullOrEmpty(normalizedUserAgent) && string.IsNullOrEmpty(normalizedRemoteAddress))
            return "";

        return HashValue($"panel-phone-v1|{normalizedRemoteAddress}|{normalizedUserAgent}");
    }

    private static string NormalizeRemoteAddress(string? remoteAddress)
    {
        var value = (remoteAddress ?? "").Trim();
        if (value.Length == 0)
            return "";

        if (IPAddress.TryParse(value, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            return address.ToString();
        }

        return NormalizeFingerprintPart(value);
    }

    private static string NormalizeFingerprintPart(string? value)
    {
        return string.Join(
            " ",
            (value ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeMachineName(string? value)
    {
        var normalized = string.Join(
            " ",
            (value ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
            return "Nexus PC";
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string GetDefaultMachineName()
    {
        if (OperatingSystem.IsMacOS())
        {
            var computerName = ReadFirstOutputLine("/usr/sbin/scutil", "--get", "ComputerName");
            if (!string.IsNullOrWhiteSpace(computerName))
                return computerName;
        }

        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
            return Environment.MachineName;

        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName))
                return hostName;
        }
        catch
        {
            // Fall through to the product fallback below.
        }

        return "Nexus PC";
    }

    private static string? ReadFirstOutputLine(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start())
                return null;

            if (!process.WaitForExit(750))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup for a one-time platform name probe.
                }
                return null;
            }

            return process.ExitCode == 0
                ? process.StandardOutput.ReadLine()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string CreateToken(int byteCount)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        return HashValue(token);
    }

    private static string HashValue(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    // -- Manual pair-code (BT-SSP-style Numeric Comparison) --------------

    /// <summary>
    /// Dashboard-side: mint a fresh 6-digit code, supersede any prior
    /// in-flight code (publishes a "cancelled" frame so an open dashboard
    /// shows the old code as expired). Returns a "remote-disabled" sentinel
    /// when the killswitch is off - the dashboard already shows the kill
    /// state, but we don't want to mint a code that the phone will then be
    /// told "remote-disabled" for on submit.
    /// </summary>
    public PanelPhonePairCodeStartResponse StartPairCode()
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhonePairCodeStartResponse { Code = "", TtlSeconds = 0 };
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expires = now + PairCodeTtlSeconds * 1000L;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var nonce = RandomNumberGenerator.GetBytes(16);

        string? supersededRequestId = null;
        lock (_pairCodeLock)
        {
            if (_pairCode is { RequestId: { Length: > 0 } prior })
                supersededRequestId = prior;

            _pairCode = new PairCodeState
            {
                Code = code,
                Nonce = nonce,
                ExpiresAt = expires,
            };
        }

        if (supersededRequestId is not null)
        {
            PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
            {
                Kind = "cancelled",
                RequestId = supersededRequestId,
                Reason = "host-started-new-code",
            });
        }

        var lanHost = LocalNetwork.GetLocalIp();
        var lanPort = HttpsPort > 0 ? HttpsPort : ServicePort;
        return new PanelPhonePairCodeStartResponse
        {
            Host = lanHost,
            Port = lanPort,
            Code = code,
            TtlSeconds = PairCodeTtlSeconds,
            ExpiresAt = expires,
        };
    }

    /// <summary>
    /// Phone-side: submit the typed code. On match the server stashes a
    /// pending request keyed by a one-shot id and broadcasts the SAS to
    /// the dashboard so the user can compare. Rate-limited per remote IP
    /// (5 attempts / 5 min lockout). Single-use: while a request is in
    /// flight (RequestId already set), subsequent submits from a different
    /// remote are rejected so an attacker who guesses the code mid-flow
    /// can't steal the handshake.
    /// REQUIRES HTTPS: the SAS binds to the captured SPKI; over plain
    /// HTTP that binding is meaningless. Reject HTTP early.
    /// </summary>
    public PanelPhonePairCodeSubmitResponse SubmitPairCode(string code, HttpContext context)
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhonePairCodeSubmitResponse { Error = "remote-disabled" };

        if (!context.Request.IsHttps)
            return new PanelPhonePairCodeSubmitResponse { Error = "https-required" };

        var remoteAddress = NormalizeRemoteAddress(context.Connection.RemoteIpAddress?.ToString());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_pairCodeLock)
        {
            PruneAttemptsLocked(nowMs);

            if (TryGetLockoutLocked(remoteAddress, nowMs) is { } lockoutSecondsRemaining)
            {
                return new PanelPhonePairCodeSubmitResponse
                {
                    Error = "rate-limited",
                    RetryAfterSeconds = lockoutSecondsRemaining,
                };
            }

            if (_pairCode is not { } state || state.ExpiresAt <= nowMs)
            {
                if (_pairCode is { RequestId: { Length: > 0 } prior })
                {
                    PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
                    {
                        Kind = "cancelled",
                        RequestId = prior,
                        Reason = "expired",
                    });
                }
                _pairCode = null;
                return new PanelPhonePairCodeSubmitResponse { Error = "no-active-code" };
            }

            // Single-use lock: a successful submit set RequestId; further
            // submits from any IP (including a second guesser) must fail
            // until /start mints a fresh code. Without this, a race
            // window between first-submit and dual-confirm lets a second
            // submitter overwrite RequestId/Sas/PhoneRemoteAddress and
            // hijack the in-flight handshake.
            if (!string.IsNullOrEmpty(state.RequestId))
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "code-in-use" };
            }

            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "invalid-code" };
            }

            var providedBytes = Encoding.UTF8.GetBytes(code);
            var expectedBytes = Encoding.UTF8.GetBytes(state.Code);
            if (providedBytes.Length != expectedBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "invalid-code" };
            }

            // Code matched. Clear lockout counter for this IP so a single
            // typo on the next manual pair from the same device doesn't
            // accumulate against a prior session's attempts.
            _pairCodeAttempts.Remove(remoteAddress);

            var requestId = CreateToken(16);
            var sas = ComputeSas(state.Code, state.Nonce, SpkiFingerprint);
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var deviceLabel = DescribeDevice(userAgent);

            state.RequestId = requestId;
            state.Sas = sas;
            state.PhoneRemoteAddress = remoteAddress;
            state.PhoneUserAgent = userAgent;
            state.ClaimedOverHttps = context.Request.IsHttps;
            state.HostApproved = false;
            state.PhoneApproved = false;

            PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
            {
                Kind = "request",
                RequestId = requestId,
                Sas = sas,
                DeviceLabel = deviceLabel,
                RemoteAddress = remoteAddress,
                UserAgent = userAgent,
                ExpiresAt = state.ExpiresAt,
            });

            return new PanelPhonePairCodeSubmitResponse
            {
                Accepted = true,
                RequestId = requestId,
                Sas = sas,
                SpkiFingerprint = SpkiFingerprint,
                MachineName = NormalizeMachineName(MachineName),
                ExpiresAt = state.ExpiresAt,
            };
        }
    }

    /// <summary>
    /// Phone-side: Wi-Fi-discovered pair handshake. Same SAS-comparison
    /// model as <see cref="SubmitPairCode"/> but with no out-of-band code
    /// — the phone found us via Bonjour, the user taps the discovered
    /// device, and the OOB authentication is the user's physical Allow
    /// click on the desktop (matching Bluetooth-style numeric comparison).
    ///
    /// Rate-limited per remote IP (same lockout as the code flow) so a
    /// malicious LAN host can't pop the pair modal in a tight loop. If a
    /// pair request is already in flight, returns "in-use"; user must
    /// resolve the existing one first.
    /// REQUIRES HTTPS for the same reason as SubmitPairCode: the SAS
    /// binds to the captured SPKI; over plain HTTP that binding is
    /// meaningless.
    /// </summary>
    public PanelPhonePairCodeSubmitResponse InitiatePairWifi(string deviceName, HttpContext context)
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhonePairCodeSubmitResponse { Error = "remote-disabled" };

        if (!context.Request.IsHttps)
            return new PanelPhonePairCodeSubmitResponse { Error = "https-required" };

        var remoteAddress = NormalizeRemoteAddress(context.Connection.RemoteIpAddress?.ToString());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expires = nowMs + PairCodeTtlSeconds * 1000L;

        lock (_pairCodeLock)
        {
            PruneAttemptsLocked(nowMs);

            if (TryGetLockoutLocked(remoteAddress, nowMs) is { } lockoutSecondsRemaining)
            {
                return new PanelPhonePairCodeSubmitResponse
                {
                    Error = "rate-limited",
                    RetryAfterSeconds = lockoutSecondsRemaining,
                };
            }

            // Single-flight: if a code-mode or wifi-mode request is in
            // flight, refuse the new initiate. Resolves the same race
            // SubmitPairCode guards against (in-flight handshake hijack).
            // Also count this as a failed attempt so a LAN scanner can't
            // spam /pair-wifi/initiate to probe whether a pair handshake
            // is in progress without hitting the rate-limit threshold.
            if (_pairCode is { ExpiresAt: var ttl } && ttl > nowMs && !string.IsNullOrEmpty(_pairCode.RequestId))
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "code-in-use" };
            }

            // Clear any leftover expired state.
            if (_pairCode is { ExpiresAt: var oldTtl } && oldTtl <= nowMs)
            {
                _pairCode = null;
            }

            // Mint a fresh SAS bound to the captured SPKI. The "code" in
            // PairCodeState is empty for wifi-initiated requests; ComputeSas
            // still derives a stable 6-digit SAS from (code || nonce || spki),
            // which the dashboard renders for the user to compare against
            // the value displayed on the phone.
            var nonce = RandomNumberGenerator.GetBytes(16);
            var requestId = CreateToken(16);
            var sas = ComputeSas(string.Empty, nonce, SpkiFingerprint);
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var deviceLabel = string.IsNullOrWhiteSpace(deviceName) ? DescribeDevice(userAgent) : deviceName.Trim();
            if (deviceLabel.Length > 64) deviceLabel = deviceLabel[..64];

            _pairCode = new PairCodeState
            {
                Code = string.Empty,
                Nonce = nonce,
                ExpiresAt = expires,
                RequestId = requestId,
                Sas = sas,
                PhoneRemoteAddress = remoteAddress,
                PhoneUserAgent = userAgent,
                ClaimedOverHttps = context.Request.IsHttps,
            };

            PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
            {
                Kind = "request",
                RequestId = requestId,
                Sas = sas,
                DeviceLabel = deviceLabel,
                RemoteAddress = remoteAddress,
                UserAgent = userAgent,
                ExpiresAt = expires,
            });

            return new PanelPhonePairCodeSubmitResponse
            {
                Accepted = true,
                RequestId = requestId,
                Sas = sas,
                SpkiFingerprint = SpkiFingerprint,
                MachineName = NormalizeMachineName(MachineName),
                ExpiresAt = expires,
            };
        }
    }

    /// <summary>
    /// Phone-side poll. The phone calls this repeatedly while the user is
    /// waiting for the host to click Allow on the dashboard. When the host
    /// has approved, the response carries the freshly-minted session token
    /// and the canonical SPKI fingerprint (so the phone can verify what it
    /// captured during the TLS handshake matches what the server says it
    /// presented). Pass <paramref name="approved"/> false to cancel a
    /// waiting request (the user closed the sheet).
    /// Requires HTTPS - the session token issued here is treated as
    /// ClaimedOverHttps so the long 30-day idle applies, and that's only
    /// safe if the channel was actually pinned.
    /// </summary>
    public PanelPhonePairCodeConfirmResponse ConfirmPairCode(string requestId, bool approved, HttpContext context)
    {
        if (!context.Request.IsHttps)
            return new PanelPhonePairCodeConfirmResponse { Status = "https-required" };

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PairCodeState? sessionToIssue = null;

        // Outcome decided inside the lock; any disk-I/O (token issuance via
        // _store.Update) happens after we drop the lock to match the
        // discipline of the QR-side Claim() flow.
        PanelPhonePairCodeConfirmResponse result;
        lock (_pairCodeLock)
        {
            if (_pairCode is not { } state || !string.Equals(state.RequestId, requestId, StringComparison.Ordinal))
                return new PanelPhonePairCodeConfirmResponse { Status = "unknown" };

            if (state.ExpiresAt <= nowMs)
            {
                _pairCode = null;
                PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "expired",
                });
                return new PanelPhonePairCodeConfirmResponse { Status = "expired" };
            }

            // Host already denied (state retained so the phone learns the
            // canonical "denied" instead of "unknown"). Drop the state on
            // the phone's next touch and report the host's decision.
            if (state.HostDenied)
            {
                _pairCode = null;
                return new PanelPhonePairCodeConfirmResponse { Status = "denied" };
            }

            // Phone user-cancel (closed the sheet or hit Cancel).
            if (!approved)
            {
                _pairCode = null;
                PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "phone-denied",
                });
                return new PanelPhonePairCodeConfirmResponse { Status = "denied" };
            }

            // The phone's presence on this endpoint IS the confirmation -
            // the user typed the code, sees the SAS, and is alive on the
            // device. Only the host's Allow gates token issuance; this
            // matches the user-visible model "compare SAS, then click
            // Allow on the system."
            if (!state.HostApproved)
                return new PanelPhonePairCodeConfirmResponse { Status = "waiting-host" };

            sessionToIssue = state;
            _pairCode = null;
            result = new PanelPhonePairCodeConfirmResponse { Status = "approved" };
        }

        if (sessionToIssue is null)
            return result;

        var (token, machineName) = IssuePairCodeSession(sessionToIssue, nowMs);
        result.Token = token;
        result.MachineName = machineName;
        result.SpkiFingerprint = SpkiFingerprint;
        return result;
    }

    /// <summary>
    /// Dashboard-side approval. If the phone has already confirmed, this
    /// triggers the token issuance on the phone's next poll. The dashboard
    /// receives the SAS via the websocket frame; the session it's authed
    /// against is the desktop session, so the dashboard already proved
    /// physical presence at the PC.
    /// </summary>
    public PanelPhonePairCodeHostDecisionResponse HostDecisionPairCode(string requestId, bool approved)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_pairCodeLock)
        {
            if (_pairCode is not { } state || !string.Equals(state.RequestId, requestId, StringComparison.Ordinal))
                return new PanelPhonePairCodeHostDecisionResponse { Status = "unknown" };

            if (state.ExpiresAt <= nowMs)
            {
                _pairCode = null;
                PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "expired",
                });
                return new PanelPhonePairCodeHostDecisionResponse { Status = "expired" };
            }

            if (!approved)
            {
                // Keep state alive so the phone's next /confirm poll returns
                // the canonical "denied" instead of "unknown". TTL still
                // reaps it; ConfirmPairCode also clears it on the phone-side
                // touch.
                state.HostDenied = true;
                PanelTopics.BroadcastPairCodeRequest(_hub, new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "host-denied",
                });
                return new PanelPhonePairCodeHostDecisionResponse { Status = "denied" };
            }

            state.HostApproved = true;
            // Token issuance happens on the phone's next /confirm poll so
            // the cookie/token lives in that response - keeps the host
            // endpoint side-effect-free for the network stack. The phone
            // is already polling at 1 Hz, so the token arrives within a
            // second of the host pressing Allow.
            return new PanelPhonePairCodeHostDecisionResponse { Status = "approved" };
        }
    }

    private (string Token, string MachineName) IssuePairCodeSession(PairCodeState state, long nowMs)
    {
        var sessionToken = CreateToken(32);
        var hash = HashToken(sessionToken);
        var userAgent = state.PhoneUserAgent;
        var remoteAddress = state.PhoneRemoteAddress;
        var deviceFingerprint = BuildDeviceFingerprint(userAgent, remoteAddress);
        var claimedOverHttps = state.ClaimedOverHttps;

        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            if (!string.IsNullOrEmpty(deviceFingerprint))
            {
                s.Auth.PanelPhoneSessions.RemoveAll(session =>
                    string.Equals(GetSessionFingerprint(session), deviceFingerprint, StringComparison.Ordinal));
            }
            s.Auth.PanelPhoneSessions.Insert(0, new PanelPhoneSessionToken
            {
                Id = CreateToken(9),
                Hash = hash,
                Name = DescribeDevice(userAgent),
                UserAgent = userAgent,
                RemoteAddress = remoteAddress,
                DeviceFingerprint = deviceFingerprint,
                CreatedAt = nowMs,
                LastSeenAt = nowMs,
                ClaimedOverHttps = claimedOverHttps,
            });
            NormalizeSessionList(s.Auth.PanelPhoneSessions, nowMs);
        });

        return (sessionToken, NormalizeMachineName(MachineName));
    }

    /// <summary>
    /// HKDF-SHA256(ikm = code, salt = nonce, info = "nexus-pair-sas-v1|" + spki)
    /// truncated to a 6-digit SAS via RFC-4226-style dynamic truncation
    /// (mask the high bit before mod 1e6 to remove the sign-bias). The
    /// security property is "binds the displayed digits to the SPKI the
    /// phone's TLS handshake actually saw" - an active MITM, who terminates
    /// TLS with a different cert, computes a different SAS, and the
    /// human-eyeballing-the-two-screens step catches the mismatch.
    /// Internal so tests can pin all three inputs and verify SPKI flip
    /// produces a different SAS.
    /// </summary>
    internal static string ComputeSas(string code, byte[] nonce, string spkiFingerprint)
    {
        var ikm = Encoding.UTF8.GetBytes(code);
        var spkiBytes = string.IsNullOrEmpty(spkiFingerprint)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(spkiFingerprint);
        var info = new byte[SasInfoPrefix.Length + spkiBytes.Length];
        Buffer.BlockCopy(SasInfoPrefix, 0, info, 0, SasInfoPrefix.Length);
        if (spkiBytes.Length > 0)
            Buffer.BlockCopy(spkiBytes, 0, info, SasInfoPrefix.Length, spkiBytes.Length);

        Span<byte> output = stackalloc byte[4];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, nonce, info);
        var truncated = BinaryPrimitives.ReadUInt32BigEndian(output) & 0x7FFFFFFF;
        return (truncated % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private int? TryGetLockoutLocked(string remoteAddress, long nowMs)
    {
        if (!_pairCodeAttempts.TryGetValue(remoteAddress, out var attempt))
            return null;
        if (attempt.LockoutUntilMs <= 0)
            return null; // attempts accumulating but no lockout yet
        if (attempt.LockoutUntilMs <= nowMs)
        {
            _pairCodeAttempts.Remove(remoteAddress);
            return null;
        }
        var remainingMs = attempt.LockoutUntilMs - nowMs;
        return (int)Math.Max(1, (remainingMs + 999) / 1000);
    }

    private void RegisterFailedAttemptLocked(string remoteAddress, long nowMs)
    {
        if (_pairCodeAttempts.TryGetValue(remoteAddress, out var attempt))
        {
            attempt.Count += 1;
            attempt.LastAttemptMs = nowMs;
        }
        else
        {
            attempt = new PairCodeAttempt { Count = 1, LastAttemptMs = nowMs };
            _pairCodeAttempts[remoteAddress] = attempt;
        }

        if (attempt.Count >= PairCodeMaxAttempts)
            attempt.LockoutUntilMs = nowMs + PairCodeLockoutMs;
    }

    private void PruneAttemptsLocked(long nowMs)
    {
        if (_pairCodeAttempts.Count == 0)
            return;
        var stale = _pairCodeAttempts
            .Where(kvp =>
                // Expired lockouts.
                (kvp.Value.LockoutUntilMs > 0 && kvp.Value.LockoutUntilMs <= nowMs)
                // Accumulating attempts that never crossed the lockout
                // threshold but have gone quiet for at least one lockout
                // window. Without this the dictionary grows for the life
                // of the process under sustained scan-then-give-up probing.
                || (kvp.Value.LockoutUntilMs <= 0 && nowMs - kvp.Value.LastAttemptMs > PairCodeLockoutMs))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in stale)
            _pairCodeAttempts.Remove(key);
    }

    private sealed class PairCodeState
    {
        public string Code { get; set; } = "";
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public long ExpiresAt { get; set; }
        public string RequestId { get; set; } = "";
        public string Sas { get; set; } = "";
        public string PhoneRemoteAddress { get; set; } = "";
        public string PhoneUserAgent { get; set; } = "";
        public bool ClaimedOverHttps { get; set; }
        public bool PhoneApproved { get; set; }
        public bool HostApproved { get; set; }
        public bool HostDenied { get; set; }
    }

    private sealed class PairCodeAttempt
    {
        public int Count { get; set; }
        public long LastAttemptMs { get; set; }
        public long LockoutUntilMs { get; set; }
    }
}
