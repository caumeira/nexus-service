using System;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// Running out of init budget must not be recorded as a verdict on the card.
///
/// A customer's cold-boot AMD iGPU handed back a working context 28.4s into a
/// 10s budget. The old code set _failed on the timeout and never looked again,
/// so every lighting mode - Static solid fills included, they all resolve to
/// simple.frag - stayed black until the service was restarted.
///
/// These assert the state machine only. EnsureInitializedLocked does start a
/// real init on the test host, whose outcome varies (a headless CI box throws in
/// milliseconds, a workstation succeeds), so nothing here asserts WHICH terminal
/// state is reached - only that waiting is never itself what produces one.
/// </summary>
public class GpuContextInitLatchTests
{
    [Fact]
    public void FreshContext_IsNeitherAvailableNorInitializingNorFailed()
    {
        using var gpu = new GpuContext(160, 90);

        Assert.False(gpu.Available);
        Assert.False(gpu.Initializing);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void StartedContext_IsInExactlyOneOfTheThreeStates()
    {
        using var gpu = new GpuContext(160, 90);
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }

        // Initializing is defined as "started, no result yet", so it must be
        // the exact complement of having reached a terminal state. The old
        // _initTried-based Available could report true against a half-built
        // context, which this rules out.
        Assert.Equal(gpu.Initializing, !(gpu.Available || gpu.Failed));
    }

    [Fact]
    public void WaitingDoesNotItselfFailTheCard()
    {
        using var gpu = new GpuContext(160, 90);
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }

        // Zero budget cannot have observed a result, so this is the timeout
        // path. Whatever the host's init is doing, elapsing the budget must not
        // move the card into Failed - that transition belongs to InitInternal
        // throwing, and latching it here is what stranded the customer.
        var failedBefore = gpu.Failed;
        gpu.WaitForInit(TimeSpan.Zero);
        gpu.WaitForInit(TimeSpan.Zero);
        Assert.Equal(failedBefore, gpu.Failed);
    }

    [Fact]
    public void ResetForRetry_IsRefusedBeforeAnyAttempt()
    {
        using var gpu = new GpuContext(160, 90);

        // Nothing has failed, so there is nothing to swap away from.
        Assert.False(gpu.ResetForRetry());
    }

    [Fact]
    public void ResetForRetry_IsRefusedWhileAnAttemptHasNotFailed()
    {
        using var gpu = new GpuContext(160, 90);
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
        gpu.WaitForInit(TimeSpan.Zero);

        // Only a terminated attempt may be swapped: a second GLFW init running
        // beside a live one shares process-global state. On a host where init
        // did fail, the retry is allowed exactly once.
        if (gpu.Failed)
        {
            Assert.True(gpu.ResetForRetry());
            Assert.False(gpu.ResetForRetry());
        }
        else
        {
            Assert.False(gpu.ResetForRetry());
        }
    }
}
