#if WINDOWS
using System.Collections.Generic;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>windowSet.snapshot</c>. Helper-to-service, one-way: the
/// set of process ids currently owning a visible top-level window with no
/// owner, refreshed on WindowSetPoller's timer. Session 0 (the LocalSystem
/// service) cannot enumerate these directly - see WindowsWindowSetProvider.
/// </summary>
public sealed class WindowSetSnapshotPayload
{
    public List<int> Pids { get; set; } = new();
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.
//
// windowSet is one-way push only, same shape as screenTime: the helper's
// WindowSetPoller pushes via HelperOutbound directly, consumed by
// WindowsWindowSetProvider's inbound envelope handler.
#endif
