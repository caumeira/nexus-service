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
    byte[] GetIcon(string exePath);
}

public interface INetworkProvider
{
    IReadOnlyList<NetworkProcessInfo> GetSnapshot();
    void SetInterval(int ms);
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
