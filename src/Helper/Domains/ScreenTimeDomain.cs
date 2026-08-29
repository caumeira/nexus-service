#if WINDOWS
namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>screenTime.session</c>. Helper-to-service, one-way.
/// Emitted when the foreground window changes (closing out the prior
/// session) or when the user goes idle long enough to clip the session
/// early.
/// </summary>
public sealed class ScreenTimeSessionPayload
{
    public string App { get; set; } = "";
    public long StartedUtcMs { get; set; }
    public long EndedUtcMs { get; set; }
}

/// <summary>
/// Payload for <c>screenTime.focus</c>. Helper-to-service, one-way.
/// Updates the service's view of the currently focused app. App=="" means
/// no focus (everything minimised, lock screen, etc.).
/// </summary>
public sealed class ScreenTimeFocusPayload
{
    public string App { get; set; } = "";
    public int Pid { get; set; }
    public long StartedUtcMs { get; set; }

    /// <summary>Full path via QueryFullProcessImageName (PROCESS_QUERY_LIMITED_INFORMATION);
    /// null when denied (EAC-protected titles, elevated processes).</summary>
    public string? ExePath { get; set; }
    /// <summary>Foreground window's client area, from GetClientRect.</summary>
    public int WinW { get; set; }
    public int WinH { get; set; }
    /// <summary>Stable monitor id (WindowsDisplayIdentity's id space, same as
    /// RawDisplayInfo.Id) for the foreground window's monitor.</summary>
    public string? MonitorDevice { get; set; }
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs.
//
// Screen-time is one-way push only. There is no helper-side Handler
// (the helper's ScreenTimePoller pushes via HelperOutbound directly)
// and no service-side Commands class (the service consumes via the
// inbound envelope event from WindowsScreenTimeProvider).
#endif
