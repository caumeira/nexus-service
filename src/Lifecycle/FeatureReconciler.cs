using System;
using System.Threading;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Drives the one-time transition side effects when a feature pillar flips
/// on or off. Per-tick gating (skip writes while a pillar stays off) lives
/// in the gated workers themselves via <see cref="FeatureGates"/>; this
/// class only handles the edge.
///
/// Serialized under a semaphore, the same shape as
/// <see cref="Nexus.Service.Mcp.McpServerHost"/>'s lifecycle lock, so two
/// near-simultaneous PATCH requests cannot interleave a suspend with a
/// resume, or race each other's before/after Features snapshot.
/// </summary>
public sealed class FeatureReconciler
{
    private readonly ILightingProvider _lighting;
    private readonly CurveEngine _curveEngine;
    private readonly SleepBlackoutCoordinator _blackout;
    private readonly IConfigStore _store;
    private readonly KeebSettingsApplier _keeb;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FeatureReconciler(
        ILightingProvider lighting, CurveEngine curveEngine, SleepBlackoutCoordinator blackout, IConfigStore store, KeebSettingsApplier keeb)
    {
        _lighting = lighting;
        _curveEngine = curveEngine;
        _blackout = blackout;
        _store = store;
        _keeb = keeb;
    }

    /// <summary>Applies the transition between two Features snapshots directly (ApplyPatch is the production entry point). Values other than the four flags are ignored.</summary>
    public void Apply(FeaturesSettings before, FeaturesSettings after)
    {
        _lock.Wait();
        try
        {
            if (before.Lighting && !after.Lighting)
            {
                _blackout.BlankOutForFeatureOff();
            }
            ApplyTransitionLocked(before, after);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Applies a settings mutation and its Features transition as one atomic
    /// unit: the before-snapshot, the mutation, the after-snapshot, and the
    /// transition side effects all run under the same lock, so a concurrent
    /// PATCH /preferences cannot read a before-snapshot this call has
    /// already superseded, or land its own mutation in between.
    ///
    /// <paramref name="lightingPatchValue"/> is the patch's intended new
    /// Lighting value, read from the request body ahead of the store
    /// mutation - the reconciler needs it before committing, to blackout
    /// while the gate still reads on (see BlankOutForFeatureOff). Null when
    /// the caller's patch does not touch the flag.
    /// </summary>
    public void ApplyPatch(Action<NexusSettings> applyPatch, bool? lightingPatchValue = null)
    {
        _lock.Wait();
        try
        {
            var before = SnapshotFeatures();
            if (before.Lighting && lightingPatchValue == false)
            {
                _blackout.BlankOutForFeatureOff();
            }
            _store.Update(applyPatch);
            var after = SnapshotFeatures();
            ApplyTransitionLocked(before, after);
        }
        finally
        {
            _lock.Release();
        }
    }

    private FeaturesSettings SnapshotFeatures()
    {
        var f = _store.Load().Features;
        return new FeaturesSettings
        {
            Lighting = f.Lighting,
            Cooling = f.Cooling,
            Monitoring = f.Monitoring,
            Diagnostics = f.Diagnostics,
        };
    }

    // Caller holds _lock. Any Lighting ON->OFF blackout has already run, by
    // this point, in Apply/ApplyPatch, before the state committed.
    private void ApplyTransitionLocked(FeaturesSettings before, FeaturesSettings after)
    {
        if (before.Lighting && !after.Lighting)
        {
            _lighting.Suspend();
        }
        else if (!before.Lighting && after.Lighting)
        {
            // No blackout release needed here: Suspend, called on the prior
            // disable, already ran LightingEngine.Stop -> ReleaseBlackoutState.
            LiveEngineSync.ApplyLighting(_store, _lighting);
            _keeb.Apply();
        }

        if (before.Cooling && !after.Cooling)
        {
            _curveEngine.RequestRelease();
        }
    }
}
