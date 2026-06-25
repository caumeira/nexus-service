using System.Collections.Generic;

namespace Nexus.Service.Models.Activity;

// ----- ScreenTime -----

public class FocusSession
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Duration Today { get; set; } = new();
    public Duration? Yesterday { get; set; }
}

public class Duration
{
    public long Total { get; set; }
    public int Milliseconds { get; set; }
    public int Seconds { get; set; }
    public int Minutes { get; set; }
    public int Hours { get; set; }
    public int Days { get; set; }
}

public class AppUsage
{
    public string Name { get; set; } = "";
    public long TotalMs { get; set; }
}

public sealed class DayBreakdown
{
    public string Date { get; set; } = "";
    public long TotalMs { get; set; }
    public int Pickups { get; set; }
    public List<AppUsage> Apps { get; set; } = new();
    public List<long> HourlyMs { get; set; } = new();
}

public sealed class DayTotal
{
    public string Date { get; set; } = "";
    public long TotalMs { get; set; }
    public int Pickups { get; set; }
}

public sealed class AppHistory
{
    public string AppName { get; set; } = "";
    public List<DayTotal> Daily { get; set; } = new();
    public long LongestSessionMs { get; set; }
    public int TotalPickups { get; set; }
    public long TotalMs { get; set; }
}

public sealed class DeleteResponse
{
    public int Deleted { get; set; }
}

public sealed class TrackingStatus
{
    public bool Enabled { get; set; } = true;
}

public sealed class SetTrackingBody
{
    public bool Enabled { get; set; } = true;
}

// ----- AppDetection -----

public class Detected
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>0 = Process, 1 = Service.</summary>
    public int Type { get; set; }
}

public class KillAppResponse : ApiResponse
{
    public bool Success { get; set; }
}

// ----- Media -----

public class MediaSession
{
    public string SourceAppName { get; set; } = "";
    public bool IsFocused { get; set; }
    public string TextColor { get; set; } = "";
    public ColorRgba DominantColor { get; set; } = new();
    public List<ColorRgba> ColorPalette { get; set; } = new();
    public MediaSong Song { get; set; } = new();
    public MediaPlayback Playback { get; set; } = new();
    public MediaControls Controls { get; set; } = new();
}

public class ColorRgba
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int A { get; set; } = 255;
}

public class MediaSong
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
}

public class MediaPlayback
{
    public bool Shuffled { get; set; }
    public string RepeatMode { get; set; } = "None";
    public bool Playing { get; set; }
    public bool Stopped { get; set; }
    public double PositionMs { get; set; }
    public double DurationMs { get; set; }
}

public class MediaControls
{
    public bool IsPrevEnabled { get; set; }
    public bool IsNextEnabled { get; set; }
    public bool IsShuffleEnabled { get; set; }
    public bool IsRepeatModeEnabled { get; set; }
    public bool IsPlayEnabled { get; set; }
    public bool IsPauseEnabled { get; set; }
    public bool IsSeekEnabled { get; set; }
}

// ----- Shortcuts -----

public class Shortcut
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public class GetAllShortcutsResponse : ApiResponse
{
    public List<Shortcut> Shortcuts { get; set; } = new();
}

public class GetShortcutResponse : ApiResponse
{
    public Shortcut? Shortcut { get; set; }
}

public class MediaControlBody
{
    public string Action { get; set; } = "";
}

// ----- Volume -----

public class VolumeState
{
    public bool Supported { get; set; }
    public double Volume { get; set; }
    public bool Muted { get; set; }
}

public class SetVolumeBody
{
    public double Volume { get; set; }
}

public class SetMutedBody
{
    public bool Muted { get; set; }
}

// ----- Network -----

public class NetworkProcessInfo
{
    public string Name { get; set; } = "";
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

