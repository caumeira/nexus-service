using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public interface IScreenTimeProvider
{
    FocusSession? GetCurrentSession();
    IReadOnlyList<AppUsage> GetTodayUsage();
}

public interface IAppDetectionProvider
{
    IReadOnlyList<Detected> GetDetected();
    bool Kill(string id);
}

public interface IMediaProvider
{
    IReadOnlyDictionary<string, MediaSession> GetSessions();
    void Control(string source, string action);
    byte[] GetAlbumArt(string source);
}

public interface IVolumeProvider
{
    VolumeState GetState();
    void SetVolume(double volume);
    void SetMuted(bool muted);
}

public interface IShortcutsProvider
{
    IReadOnlyList<Shortcut> GetAll();
    Shortcut? GetById(string targetId);
    byte[] GetIcon(string targetId);
    bool Launch(string targetId);
}

/// <summary>Extracts a PNG icon for a running process's executable, keyed by
/// its full path (not a shortcut targetId - a live process rarely has a
/// matching Start-Menu shortcut). Empty bytes when unsupported or
/// unresolvable.</summary>
public interface IProcessIconProvider
{
    /// <summary>Null means extraction could not even be attempted (e.g. the
    /// Windows-only helper is not connected yet, or its RPC round trip
    /// timed out) - a transient condition callers must not cache as a
    /// negative result. Empty bytes means extraction ran and found no
    /// icon.</summary>
    byte[]? GetIcon(string exePath);
}

/// <summary>Kill / reveal-in-file-manager actions for a live process, driven
/// by the monitoring sidebar. Windows routes both through the user-session
/// helper so the OS enforces the console user's own privileges - the
/// LocalSystem service never acts on a process directly. macOS/Linux already
/// run in the user session, so they act directly.</summary>
public interface IProcessActionsProvider
{
    /// <summary>False when there is currently no way to perform an action
    /// (Windows: no console-user helper connected) - the route surfaces this
    /// as a 503. Always true on platforms that act directly.</summary>
    bool IsAvailable { get; }

    /// <summary>Kills every live instance of processName. Killed/Failed count
    /// individual kill attempts (failed covers e.g. access denied); both are
    /// 0 when no matching process was found or IsAvailable is false.</summary>
    Task<(int Killed, int Failed)> KillAsync(string processName);

    /// <summary>Reveals exePath in the OS file manager with the file
    /// selected. False on failure, including IsAvailable being false.</summary>
    Task<bool> OpenLocationAsync(string exePath);
}

public interface INetworkProvider
{
    IReadOnlyList<NetworkProcessInfo> GetSnapshot();
    void SetInterval(int ms);
}

/// <summary>Reports whether a live pid currently owns a visible top-level
/// window (Task-Manager-style App classification) for ProcessMonitor's
/// isApp field. LocalSystem in Session 0 cannot enumerate the interactive
/// desktop's windows, so the only real implementation is Windows-only and
/// backed by the user-session helper; ProcessMonitor treats an unregistered
/// provider (non-Windows, or before any helper connects) as "nothing is
/// windowed" rather than an error.</summary>
public interface IWindowSetProvider
{
    bool IsWindowed(int pid);
}

public interface IBeatsProvider : IDisposable
{
    /// <summary>Start capturing audio and analysing beats. Idempotent.</summary>
    void Start();

    /// <summary>Stop capturing. Idempotent.</summary>
    void Stop();

    /// <summary>Raised each analysis window (~50ms) after AudioState is refreshed.</summary>
    event Action? OnBeat;
}
