using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>Fallback for platforms without a topology source; also the DI seam tests replace.</summary>
public sealed class StubDisplayTopologyProvider : IDisplayTopologyProvider
{
    public bool PositionsAvailable => false;

    public IReadOnlyList<RawDisplayInfo>? Enumerate() => new List<RawDisplayInfo>();
}
