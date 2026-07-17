#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Serialization;

namespace Nexus.Service.Activity;

/// <summary>
/// Latest set of process ids owning a visible top-level window, pushed by
/// the user-session helper's WindowSetPoller (LocalSystem in Session 0
/// cannot enumerate the interactive desktop's windows). Backs
/// ProcessMonitor's isApp classification; an empty snapshot (no helper
/// connected yet) reads as "nothing windowed" rather than an error.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowSetProvider : IWindowSetProvider, IDisposable
{
    private readonly HelperRegistry _helper;
    private readonly object _lock = new();
    private HashSet<int> _snapshot = new();

    public WindowsWindowSetProvider(HelperRegistry helper)
    {
        _helper = helper;
        _helper.InboundEnvelope += OnEnvelope;
    }

    private void OnEnvelope(HelperConnection _, HelperEnvelope env)
    {
        if (env.Type != "windowSet.snapshot" || env.Payload is null) return;
        try
        {
            var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.WindowSetSnapshotPayload);
            if (p is null) return;
            var fresh = new HashSet<int>(p.Pids);
            lock (_lock) { _snapshot = fresh; }
        }
        catch { }
    }

    public bool IsWindowed(int pid)
    {
        lock (_lock) { return _snapshot.Contains(pid); }
    }

    public void Dispose() => _helper.InboundEnvelope -= OnEnvelope;
}
#endif
