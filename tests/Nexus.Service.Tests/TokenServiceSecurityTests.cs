using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// The load-bearing property of <see cref="TokenService.Validate"/> is that it
/// compares in constant time (<c>CryptographicOperations.FixedTimeEquals</c>),
/// which also means a length mismatch must return false rather than throw.
/// </summary>
public sealed class TokenServiceSecurityTests : IDisposable
{
    private readonly string _dir;
    private readonly TokenService _tokens;

    public TokenServiceSecurityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-token-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _tokens = new TokenService(new JsonConfigStore(Path.Combine(_dir, "settings.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Validates_the_real_token()
        => Assert.True(_tokens.Validate(_tokens.Token));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public void Rejects_empty_or_short_tokens(string? bad)
        => Assert.False(_tokens.Validate(bad));

    [Fact]
    public void Rejects_different_length_token_without_throwing()
    {
        Assert.False(_tokens.Validate(_tokens.Token + "x"));
        Assert.False(_tokens.Validate(_tokens.Token[..^1]));
    }

    [Fact]
    public void Rejects_same_length_wrong_token()
    {
        var wrong = new string('A', _tokens.Token.Length);
        Assert.NotEqual(_tokens.Token, wrong);
        Assert.False(_tokens.Validate(wrong));
    }
}
