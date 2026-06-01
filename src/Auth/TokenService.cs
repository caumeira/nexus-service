using System;
using System.Security.Cryptography;
using System.Text;
using Nexus.Service.Persistence;

namespace Nexus.Service.Auth;

/// <summary>
/// Manages the local pairing token. A 32-byte URL-safe token is generated at
/// first run and persisted in settings.json. The SPA retrieves it once via the
/// /pair endpoint (localhost-only), then sends it as a Bearer header on every
/// subsequent request.
/// </summary>
public sealed class TokenService
{
    private readonly IConfigStore _store;
    private string? _cached;

    public TokenService(IConfigStore store)
    {
        _store = store;
        EnsureToken();
    }

    /// <summary>The current valid token. Never null after construction.</summary>
    public string Token => _cached!;

    /// <summary>Returns true if the provided token matches the stored one.</summary>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        var provided = Encoding.UTF8.GetBytes(token);
        var expected = Encoding.UTF8.GetBytes(Token);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private void EnsureToken()
    {
        var settings = _store.Load();
        if (!string.IsNullOrEmpty(settings.Auth?.Token))
        {
            _cached = settings.Auth.Token;
            return;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        _cached = Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.Token = _cached;
        });
    }
}
