using Nexus.Service.Persistence;

namespace Nexus.Service.Lifecycle;

/// <summary>Wire names for the four feature pillars, shared by route 409 bodies and MCP tool messages.</summary>
public static class FeatureNames
{
    public const string Lighting = "lighting";
    public const string Cooling = "cooling";
    public const string Monitoring = "monitoring";
    public const string Diagnostics = "diagnostics";
}

/// <summary>
/// Read-only per-tick gate for the four feature pillars. Backed by
/// IConfigStore.Load(), which is already cached, so every property read is
/// free. Constructors across the workers this gates take it as an optional
/// trailing parameter defaulting to <see cref="AllEnabled"/>, so the many
/// existing unit tests that construct a worker directly and do not exercise
/// gating keep working unchanged; production DI always resolves the real
/// singleton registered in AddNexusCore.
/// </summary>
public sealed class FeatureGates
{
    /// <summary>Fallback used when no store-backed instance was supplied. Every flag reads true.</summary>
    public static readonly FeatureGates AllEnabled = new();

    private readonly IConfigStore? _store;

    public FeatureGates(IConfigStore store)
    {
        _store = store;
    }

    private FeatureGates()
    {
    }

    public bool Lighting => _store?.Load().Features.Lighting ?? true;
    public bool Cooling => _store?.Load().Features.Cooling ?? true;
    public bool Monitoring => _store?.Load().Features.Monitoring ?? true;
    public bool Diagnostics => _store?.Load().Features.Diagnostics ?? true;
}
