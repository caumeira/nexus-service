using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests;

public sealed class LinuxNotifyTests
{
    // On the (non-root, non-Linux) test host WrapForUser returns the command
    // unwrapped, so we can assert the notify-send invocation directly.
    [Fact]
    public void BuildCommand_ProducesNotifySendInvocation()
    {
        var (file, args) = LinuxNotify.BuildCommand("Pair request", "Device X wants to pair. Code: 1234");

        Assert.Equal("notify-send", file);
        Assert.Equal(new[] { "-a", "Nexus", "-i", "nexus", "Pair request", "Device X wants to pair. Code: 1234" },
            args.ToArray());
    }

    [Fact]
    public void BuildCommand_KeepsSummaryAndBodyAsDistinctTrailingArgs()
    {
        // Summary and body must stay separate argv entries so a body with spaces
        // isn't swallowed into the summary.
        var (_, args) = LinuxNotify.BuildCommand("S", "multi word body");
        Assert.Equal("S", args[^2]);
        Assert.Equal("multi word body", args[^1]);
    }
}
