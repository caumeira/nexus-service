using System;
using System.Threading;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

/// <summary>
/// Running out of init budget must not be recorded as a verdict on the card: a
/// customer's context arrived at 28.4s against a 10s budget, and every lighting
/// mode is shader-rendered, so latching left every LED black until a restart.
///
/// The init body is substituted throughout - a real GL init is host-dependent
/// and would put concurrent glfwInit calls into a parallelized suite.
/// </summary>
public class GpuContextInitLatchTests
{
    private static GpuContext WithInit(Action init) => new(160, 90, null, init);

    /// <summary>Init that blocks until the test releases it.</summary>
    private static GpuContext Blocking(ManualResetEventSlim gate) =>
        WithInit(() => gate.Wait(TimeSpan.FromSeconds(30)));

    private static void Start(GpuContext gpu)
    {
        lock (gpu.Lock)
        {
            gpu.EnsureInitializedLocked();
        }
    }

    [Fact]
    public void FreshContext_IsNeitherAvailableNorInitializingNorFailed()
    {
        using var gpu = WithInit(() => { });

        Assert.False(gpu.Available);
        Assert.False(gpu.Initializing);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void ElapsedBudget_LeavesTheAttemptRunning()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);

        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));
        Assert.False(gpu.Failed);
        Assert.True(gpu.Initializing);
        gate.Set();
    }

    [Fact]
    public void ContextThatLandsAfterTheBudget_StillBecomesAvailable()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));

        gate.Set();

        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        Assert.True(gpu.Available);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void InitThatThrows_IsTerminal()
    {
        using var gpu = WithInit(() => throw new InvalidOperationException("no adapter"));
        Start(gpu);

        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        Assert.True(gpu.Failed);
        Assert.False(gpu.Available);
        Assert.False(gpu.Initializing);
    }

    [Fact]
    public void AbandonInit_TurnsAnEndlessWaitIntoAFailure()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromMilliseconds(50)));

        gpu.AbandonInit();

        Assert.True(gpu.Failed);
        Assert.False(gpu.Initializing);
        gate.Set();
    }

    [Fact]
    public void AbandonInit_DoesNotDemoteAContextThatAlreadyLanded()
    {
        using var gpu = WithInit(() => { });
        Start(gpu);
        Assert.True(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        gpu.AbandonInit();

        Assert.True(gpu.Available);
        Assert.False(gpu.Failed);
    }

    [Fact]
    public void ResetForRetry_IsRefusedBeforeAnyAttempt()
    {
        using var gpu = WithInit(() => { });

        Assert.False(gpu.ResetForRetry());
    }

    [Fact]
    public void ResetForRetry_IsRefusedWhileAnAttemptIsStillRunning()
    {
        using var gate = new ManualResetEventSlim(false);
        using var gpu = Blocking(gate);
        Start(gpu);
        gpu.WaitForInit(TimeSpan.FromMilliseconds(50));

        // A second GLFW init beside a live one shares process-global state.
        Assert.False(gpu.ResetForRetry());
        gate.Set();
    }

    [Fact]
    public void ResetForRetry_AllowsExactlyOneRetryAfterAFailure()
    {
        var attempts = 0;
        using var gpu = WithInit(() =>
        {
            attempts++;
            throw new InvalidOperationException("no adapter");
        });
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));

        Assert.True(gpu.ResetForRetry());
        // Does not start the attempt itself: the caller sets the OS GPU
        // preference first, which is read at context-creation time.
        Assert.Equal(1, attempts);
        Start(gpu);
        Assert.False(gpu.WaitForInit(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, attempts);

        Assert.False(gpu.ResetForRetry());
    }
}
