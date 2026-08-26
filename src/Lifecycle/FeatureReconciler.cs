using System.Threading;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Drives the one-time transition side effects when a feature pillar flips
/// on or off, called only from the PATCH /preferences handler after the new
/// flags are persisted. Per-tick gating (skip writes while a pillar stays
/// off) lives in the gated workers themselves via <see cref="FeatureGates"/>;
/// this class only handles the edge.
///
/// Serialized under a semaphore, the same shape as
/// <see cref="Nexus.Service.Mcp.McpServerHost"/>'s lifecycle lock, so two
/// near-simultaneous PATCH requests cannot interleave a suspend with a
/// resume.
/// </summary>
public sealed class FeatureReconciler
{
    private readonly ILightingProvider _lighting;
    private readonly IFanControlProvider _fans;
    private readonly IConfigStore _store;
    private readonly KeebSettingsApplier _keeb;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FeatureReconciler(ILightingProvider lighting, IFanControlProvider fans, IConfigStore store, KeebSettingsApplier keeb)
    {
        _lighting = lighting;
        _fans = fans;
        _store = store;
        _keeb = keeb;
    }

    /// <summary>Applies the transition between two Features snapshots. Values other than the four flags are ignored.</summary>
    public void Apply(FeaturesSettings before, FeaturesSettings after)
    {
        _lock.Wait();
        try
        {
            if (before.Lighting && !after.Lighting)
            {
                _lighting.Suspend();
            }
            else if (!before.Lighting && after.Lighting)
            {
                LiveEngineSync.ApplyLighting(_store, _lighting);
                _keeb.Apply();
            }

            if (before.Cooling && !after.Cooling)
            {
                _fans.ReleaseAll();
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
