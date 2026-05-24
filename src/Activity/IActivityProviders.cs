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

    /// <summary>Raised each analysis window (~50ms) with the latest beat metrics.</summary>
    event Action<MusicResult>? OnBeat;
}
