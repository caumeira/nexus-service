using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public sealed class StubScreenTimeProvider : IScreenTimeProvider
{
    public FocusSession? GetCurrentSession() => null;
    public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
}

public sealed class StubAppDetectionProvider : IAppDetectionProvider
{
    public IReadOnlyList<Detected> GetDetected() => Array.Empty<Detected>();
    public bool Kill(string id) => ProcessKiller.Kill(id);
}

public sealed class StubMediaProvider : IMediaProvider
{
    private static readonly IReadOnlyDictionary<string, MediaSession> Empty =
        new Dictionary<string, MediaSession>();

    public IReadOnlyDictionary<string, MediaSession> GetSessions() => Empty;
    public void Control(string source, string action) { }
    public byte[] GetAlbumArt(string source) => Array.Empty<byte>();
}

public sealed class StubShortcutsProvider : IShortcutsProvider
{
    public IReadOnlyList<Shortcut> GetAll() => Array.Empty<Shortcut>();
    public Shortcut? GetById(string targetId) => null;
    public byte[] GetIcon(string targetId) => Array.Empty<byte>();
    public bool Launch(string targetId) => false;
}

public sealed class StubProcessIconProvider : IProcessIconProvider
{
    // Unsupported off Windows is a stable platform trait, not a transient
    // failure - empty (not null) so the route caches it and never retries.
    public byte[]? GetIcon(string exePath) => Array.Empty<byte>();
}

public sealed class StubNetworkProvider : INetworkProvider
{
    public IReadOnlyList<NetworkProcessInfo> GetSnapshot() => Array.Empty<NetworkProcessInfo>();
    public void SetInterval(int ms) { }
}

public sealed class StubBeatsProvider : IBeatsProvider
{
    public event Action? OnBeat { add { } remove { } }
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
