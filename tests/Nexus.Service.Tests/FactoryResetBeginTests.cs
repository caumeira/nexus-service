using Nexus.Service.Lifecycle;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers only the finalizer-claim latch: a held claim must short-circuit
/// Begin before any process spawn (two live finalizers are mutual SIGKILL
/// targets of the macOS survivor sweep). The spawn path itself is never
/// exercised here - a real Begin would launch the testhost as a finalizer.
/// </summary>
public class FactoryResetBeginTests : IDisposable
{
    public FactoryResetBeginTests() => FactoryReset.ResetFinalizerClaimForTests();

    public void Dispose() => FactoryReset.ResetFinalizerClaimForTests();

    [Fact]
    public void Begin_refuses_while_a_finalizer_is_already_claimed()
    {
        FactoryReset.ClaimFinalizerForTests();

        Assert.False(FactoryReset.Begin());
        Assert.False(FactoryReset.Begin(wipe: false));
        Assert.True(FactoryReset.FinalizerClaimed);
    }

    [Fact]
    public void Claim_starts_released()
    {
        Assert.False(FactoryReset.FinalizerClaimed);
    }
}
