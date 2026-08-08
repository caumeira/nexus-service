#if !WINDOWS
using Nexus.Service.Lifecycle;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Off-Windows, ReadAutoStart/WriteAutoStart delegate straight to
/// IStartupProvider instead of the Windows SCM registry/sc.exe path.
/// </summary>
public sealed class ServiceControlRoutesTests
{
    private sealed class FakeStartupProvider : IStartupProvider
    {
        public bool Enabled;
        public string? LastPath;
        public string? LastArguments;
        public bool SetEnabledResult = true;

        public bool IsEnabled() => Enabled;

        public bool SetEnabled(bool enabled, string path, string arguments)
        {
            LastPath = path;
            LastArguments = arguments;
            Enabled = enabled;
            return SetEnabledResult;
        }
    }

    [Fact]
    public void ReadAutoStart_ReturnsTheProvidersEnabledState()
    {
        var provider = new FakeStartupProvider { Enabled = true };

        Assert.True(ServiceControlRoutes.ReadAutoStart(provider));

        provider.Enabled = false;
        Assert.False(ServiceControlRoutes.ReadAutoStart(provider));
    }

    [Fact]
    public void WriteAutoStart_PassesTheCurrentExecutableAndServiceArgument()
    {
        var provider = new FakeStartupProvider();

        Assert.True(ServiceControlRoutes.WriteAutoStart(true, provider));

        Assert.True(provider.Enabled);
        Assert.Equal("--service", provider.LastArguments);
        Assert.False(string.IsNullOrEmpty(provider.LastPath));
    }

    [Fact]
    public void WriteAutoStart_ReturnsFalse_WhenTheProviderFails()
    {
        var provider = new FakeStartupProvider { SetEnabledResult = false };

        Assert.False(ServiceControlRoutes.WriteAutoStart(true, provider));
    }
}
#endif
