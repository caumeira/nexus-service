using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Nexus.Service.Widgets;

/// <summary>
/// Short-lived URL-path tokens that authorise a single Tier 2 widget worker
/// to fetch its own bundled .js/.mjs files over <c>/apps-api/code/{token}/{path}</c>.
///
/// <para>
/// Why this exists: module workers (<c>new Worker(url, { type: "module" })</c>)
/// import sibling files through the browser's ESM loader, which doesn't carry
/// the panel's Bearer token. Embedding a per-spawn token in the URL path lets
/// relative <c>import "./lib/x.js"</c> inherit the token automatically without
/// us needing cookie-based auth on the panel surface.
/// </para>
///
/// <para>
/// The token is a 24-byte cryptographically-random base64url string. Each
/// session is bound to a single widget id; the file-serving route only resolves
/// paths under that widget's <c>RootPath</c> via <see cref="AppRoutes.ResolveBundleFile"/>.
/// </para>
/// </summary>
public sealed class AppCodeSessionService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public string Create(string widgetId, TimeSpan? lifetime = null)
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var session = new Session
        {
            AppId = widgetId,
            ExpiresAt = DateTimeOffset.UtcNow + (lifetime ?? DefaultLifetime),
        };
        _sessions[token] = session;
        SweepExpired();
        return token;
    }

    /// <summary>
    /// Validate that <paramref name="token"/> exists, has not expired, and is
    /// bound to a widget. Returns the widget id on success so the caller can
    /// scope the file lookup; null otherwise.
    /// </summary>
    public string? Resolve(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }
        return session.AppId;
    }

    public void Revoke(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        _sessions.TryRemove(token, out _);
    }

    /// <summary>Drop expired entries. Called on each Create() so the dictionary
    /// can't grow unbounded under abuse.</summary>
    private void SweepExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (k, v) in _sessions)
        {
            if (v.ExpiresAt <= now) _sessions.TryRemove(k, out _);
        }
    }

    private sealed class Session
    {
        public string AppId { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
    }
}
