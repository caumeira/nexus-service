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
/// ProcessMonitor's isApp classification; an empty snapshot - before any
/// helper has connected, or after one drops without a clean disconnect -
/// reads as "nothing windowed" rather than an error.
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
        _helper.Disconnected += OnDisconnected;
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

    // A crashed (not restarted) helper leaves no envelope to react to, so
    // without this the last snapshot would keep answering IsWindowed forever
    // - including for a pid the OS reuses during the outage. Reconnect
    // repopulates it via the poller's own resend-on-reconnect.
    private void OnDisconnected(HelperConnection _)
    {
        lock (_lock) { _snapshot = new HashSet<int>(); }
    }

    public bool IsWindowed(int pid)
    {
        lock (_lock) { return _snapshot.Contains(pid); }
    }

    /// <summary>Test-only seam: drives the real OnEnvelope handler.
    /// HelperConnection's constructor requires a live named pipe, so tests
    /// pass null - the handler never reads its first parameter.</summary>
    internal void IngestEnvelopeForTest(HelperEnvelope env) => OnEnvelope(null!, env);

    /// <summary>Test-only seam: drives the real OnDisconnected handler,
    /// same null-HelperConnection reasoning as IngestEnvelopeForTest.</summary>
    internal void DisconnectForTest() => OnDisconnected(null!);

    public void Dispose()
    {
        _helper.InboundEnvelope -= OnEnvelope;
        _helper.Disconnected -= OnDisconnected;
    }
}
#endif
