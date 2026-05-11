using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Qos.Service.Models.Panel;
using Qos.Service.Net;
using Qos.Service.Persistence;

namespace Qos.Service.Panel;

public sealed class PanelPhonePairingService
{
    public const string PresenceTopic = "panel/phone/presence";
    public const string SessionCookieName = "qos_phone_token";
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

    private readonly IConfigStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _pairTokens = new(StringComparer.Ordinal);

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
    public string PublicLinkHost { get; set; } = "nexusqos.com";

    /// <summary>
    /// Resolves the user-visible host PC name. Reads
    /// QosSettings.HostDisplayName each call so a settings update is
    /// reflected immediately in the next QR / claim / /ping payload, then
    /// falls back to the OS-reported machine name when the override is empty.
    /// </summary>
    public string MachineName => ResolveMachineName();

    public PanelPhonePairingService(IConfigStore store)
    {
        _store = store;
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

    public bool RevokeSession(string id)
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

    public int RevokeAllSessions()
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
        return removed;
    }

    public bool ValidateSessionToken(string? token)
        => ValidateSessionToken(token, context: null);

    public bool ValidateSessionToken(string? token, HttpContext? context)
    {
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
        if (userAgent.Contains("qOS/", StringComparison.OrdinalIgnoreCase) &&
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
            return "qOS PC";
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

        return "qOS PC";
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

}
