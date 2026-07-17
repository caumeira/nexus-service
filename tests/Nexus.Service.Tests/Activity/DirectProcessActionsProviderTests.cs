using System;
using System.Threading.Tasks;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class DirectProcessActionsProviderTests
{
    [Fact]
    public void IsAvailable_IsAlwaysTrue()
    {
        var provider = new DirectProcessActionsProvider();

        Assert.True(provider.IsAvailable);
    }

    [Fact]
    public async Task KillAsync_ReturnsZeroZero_WhenNoProcessMatchesTheName()
    {
        var provider = new DirectProcessActionsProvider();

        var (killed, failed) = await provider.KillAsync("nexus-test-no-such-process-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(0, killed);
        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task OpenLocationAsync_ReturnsFalse_WhenTheFileDoesNotExist()
    {
        var provider = new DirectProcessActionsProvider();

        var ok = await provider.OpenLocationAsync("/nonexistent/path/to/app.exe");

        Assert.False(ok);
    }

    [Fact]
    public async Task OpenLocationAsync_ReturnsFalse_ForAnEmptyPath()
    {
        var provider = new DirectProcessActionsProvider();

        var ok = await provider.OpenLocationAsync("");

        Assert.False(ok);
    }
}
