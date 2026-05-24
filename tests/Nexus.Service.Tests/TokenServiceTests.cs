using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class TokenServiceTests
{
    [Fact]
    public void Token_IsGeneratedOnFirstRun()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        Assert.False(string.IsNullOrEmpty(service.Token));
        Assert.True(service.Token.Length > 20, "Token should be at least 20 chars (32 bytes base64)");
    }

    [Fact]
    public void Token_IsPersistedToStore()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        var settings = store.Load();
        Assert.Equal(service.Token, settings.Auth?.Token);
    }

    [Fact]
    public void Token_IsUrlSafe()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        Assert.DoesNotContain("+", service.Token);
        Assert.DoesNotContain("/", service.Token);
        Assert.DoesNotContain("=", service.Token);
    }

    [Fact]
    public void Token_IsReusedFromExistingSettings()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Auth = new AuthSettings { Token = "my-existing-token" };
        });

        var service = new TokenService(store);
        Assert.Equal("my-existing-token", service.Token);
    }

    [Fact]
    public void Validate_ReturnsTrueForCorrectToken()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        Assert.True(service.Validate(service.Token));
    }

    [Fact]
    public void Validate_ReturnsFalseForWrongToken()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        Assert.False(service.Validate("wrong-token"));
    }

    [Fact]
    public void Validate_ReturnsFalseForNull()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        Assert.False(service.Validate(null));
    }

    [Fact]
    public void Validate_ReturnsFalseForEmpty()
    {
        var store = new InMemoryConfigStore();
        var service = new TokenService(store);

        Assert.False(service.Validate(""));
    }
}

/// <summary>
/// In-memory IConfigStore for unit tests — no disk I/O.
/// </summary>
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
