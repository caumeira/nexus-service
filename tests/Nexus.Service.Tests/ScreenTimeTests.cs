using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests;

public class ScreenTimeTests
{
    [Fact]
    public void StubProvider_GetCurrentSession_ReturnsNull()
    {
        var provider = new StubScreenTimeProvider();

        var session = provider.GetCurrentSession();

        Assert.Null(session);
    }

    [Fact]
    public void StubProvider_GetTodayUsage_ReturnsEmpty()
    {
        var provider = new StubScreenTimeProvider();

        var usage = provider.GetTodayUsage();

        Assert.Empty(usage);
    }

}
