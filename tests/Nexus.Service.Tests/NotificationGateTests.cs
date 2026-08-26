using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Notifications;
using Xunit;

namespace Nexus.Service.Tests;

[Collection("NotificationGate")]
public class NotificationGateTests
{
    [Fact]
    public async Task HeldNotificationsGoOutInOrderWhenOnboardingEnds()
    {
        NotificationGate.ResetForTests();
        var sent = new List<string>();
        NotificationGate.HoldOrRun(() => { sent.Add("a"); return Task.CompletedTask; }, out _);
        NotificationGate.HoldOrRun(() => { sent.Add("b"); return Task.CompletedTask; }, out _);
        Assert.Empty(sent);

        await NotificationGate.ReleaseAsync();
        Assert.Equal(new[] { "a", "b" }, sent);
    }

    [Fact]
    public void HoldOrRun_ReportsWhetherItHeld()
    {
        NotificationGate.ResetForTests();
        Assert.True(NotificationGate.HoldOrRun(() => Task.CompletedTask, out _));
        NotificationGate.Initialize(onboardingComplete: true);
        Assert.False(NotificationGate.HoldOrRun(() => Task.CompletedTask, out _));
    }

    [Fact]
    public async Task AnOpenGateSendsStraightThrough()
    {
        NotificationGate.ResetForTests();
        NotificationGate.Initialize(onboardingComplete: true);
        var sent = false;
        NotificationGate.HoldOrRun(() => { sent = true; return Task.CompletedTask; }, out var task);
        await task;
        Assert.True(sent);
    }

    [Fact]
    public async Task AThrowingNoticeDoesNotStrandTheRest()
    {
        NotificationGate.ResetForTests();
        var sent = new List<string>();
        NotificationGate.HoldOrRun(() => throw new System.InvalidOperationException("boom"), out _);
        NotificationGate.HoldOrRun(() => { sent.Add("after"); return Task.CompletedTask; }, out _);

        await NotificationGate.ReleaseAsync();
        Assert.Equal(new[] { "after" }, sent);
    }

    [Fact]
    public async Task ReleaseIsIdempotent()
    {
        NotificationGate.ResetForTests();
        var count = 0;
        NotificationGate.HoldOrRun(() => { count++; return Task.CompletedTask; }, out _);
        await NotificationGate.ReleaseAsync();
        await NotificationGate.ReleaseAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public void AResetClosesTheGateAgainForAReplayedOnboarding()
    {
        NotificationGate.ResetForTests();
        NotificationGate.Initialize(onboardingComplete: true);
        Assert.True(NotificationGate.IsOpen);

        // What POST /onboarding/reset does.
        NotificationGate.Initialize(onboardingComplete: false);
        Assert.False(NotificationGate.IsOpen);
        Assert.True(NotificationGate.HoldOrRun(() => Task.CompletedTask, out _));
    }

    [Fact]
    public void TheBacklogIsBounded()
    {
        NotificationGate.ResetForTests();
        for (var i = 0; i < 40; i++)
        {
            NotificationGate.HoldOrRun(() => Task.CompletedTask, out _);
        }
        Assert.Equal(16, NotificationGate.HeldCount);
    }
}
