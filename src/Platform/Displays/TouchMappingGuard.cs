using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Platform.Displays;

public enum TouchMappingPassResult
{
    NoHelper,
    NoPanel,
    NoDigitizer,
    AlreadyCorrect,
    Repaired,
    Failed,
}

/// <summary>
/// Detects and repairs a mis-mapped touch digitizer: Windows can associate a
/// touch panel's digitizer with the wrong monitor (documented fallback when
/// container-id matching fails - see plans/touch-mapping-auto-repair.md
/// section 1), which sends touch input to the wrong screen. Registry write
/// and devnode restart both run session-independent, so this class needs no
/// user-session access itself; ITouchMapSnapshotSource is the only dependency
/// that does (implemented via the helper on Windows).
/// </summary>
public sealed class TouchMappingGuard
{
    private static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultVerifyPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly ITouchMapSnapshotSource _snapshotSource;
    private readonly IDigimonRegistryWriter _registryWriter;
    private readonly ITouchDigitizerDevnodeRestarter _devnodeRestarter;
    private readonly TimeSpan _verifyTimeout;
    private readonly TimeSpan _verifyPollInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>verifyTimeout/verifyPollInterval override the bench-derived
    /// defaults for tests; production callers (DI) always get the defaults.</summary>
    public TouchMappingGuard(
        ITouchMapSnapshotSource snapshotSource,
        IDigimonRegistryWriter registryWriter,
        ITouchDigitizerDevnodeRestarter devnodeRestarter,
        TimeSpan? verifyTimeout = null,
        TimeSpan? verifyPollInterval = null)
    {
        _snapshotSource = snapshotSource;
        _registryWriter = registryWriter;
        _devnodeRestarter = devnodeRestarter;
        _verifyTimeout = verifyTimeout ?? DefaultVerifyTimeout;
        _verifyPollInterval = verifyPollInterval ?? DefaultVerifyPollInterval;
    }

    /// <summary>
    /// One detect-and-repair pass. Serialized against concurrent callers (the
    /// background trigger and the manual route can fire close together) so
    /// two passes never race a registry write.
    /// </summary>
    public async Task<TouchMappingPassResult> RunPassAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await _snapshotSource.GetSnapshotAsync(ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                Log("no helper connected; skipping");
                return TouchMappingPassResult.NoHelper;
            }

            var (outcome, plan) = TouchMappingDecision.Decide(snapshot);
            switch (outcome)
            {
                case TouchMappingOutcome.NoPanel:
                    Log("no catalog touch panel display attached; no-op");
                    return TouchMappingPassResult.NoPanel;
                case TouchMappingOutcome.NoDigitizer:
                    Log("catalog panel attached but its digitizer is not present; no-op");
                    return TouchMappingPassResult.NoDigitizer;
                case TouchMappingOutcome.AlreadyCorrect:
                    Log("digitizer already mapped to its panel display; no-op");
                    return TouchMappingPassResult.AlreadyCorrect;
            }

            var repairPlan = plan!;
            Log($"mismatch detected, repairing: digitizer '{repairPlan.DigitizerInterfacePath}' -> display '{repairPlan.PanelDisplayId}'");
            _registryWriter.Write(repairPlan.DigitizerInterfacePath, repairPlan.PanelMonitorInterfacePath);
            if (!_devnodeRestarter.Restart(repairPlan.DigitizerInterfacePath))
            {
                Log("devnode restart failed");
                return TouchMappingPassResult.Failed;
            }

            // The registry write alone is inert; Windows only re-reads the
            // mapping at digitizer arrival (bench-proven, see
            // plans/touch-mapping-auto-repair.md section 4a). The restart is
            // sub-second on the bench but carries no completion signal this
            // process can await, so poll the bounded window instead.
            var deadline = DateTime.UtcNow + _verifyTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(_verifyPollInterval, ct).ConfigureAwait(false);
                var verify = await _snapshotSource.GetSnapshotAsync(ct).ConfigureAwait(false);
                if (verify is null) continue;
                var (verifyOutcome, _) = TouchMappingDecision.Decide(verify);
                if (verifyOutcome == TouchMappingOutcome.AlreadyCorrect)
                {
                    Log("repair verified");
                    return TouchMappingPassResult.Repaired;
                }
            }
            Log("repair could not be verified within the timeout");
            return TouchMappingPassResult.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Log(string message) => ServiceLog.Info($"[touch-map] {message}");
}
