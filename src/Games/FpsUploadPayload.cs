using System.Collections.Generic;

namespace Nexus.Service.Games;

/// <summary>
/// Wire body for POST /telemetry/fps-sessions, camelCase via AppJsonContext.
/// Field names are a pinned cross-repo contract with nexus-api's
/// UploadFpsSessionsDto - ValidationPipe runs forbidNonWhitelisted, so an
/// extra or misspelled field 400s the whole batch.
/// </summary>
public sealed class FpsUploadPayload
{
    public string InstallId { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Arch { get; set; } = "";
    public FpsUploadHardware Hardware { get; set; } = new();
    public List<FpsUploadSession> Sessions { get; set; } = new();
}

/// <summary>Coarse rig summary at upload time; every field is optional on the
/// api side, so an empty string or zero is accepted when a probe failed.</summary>
public sealed class FpsUploadHardware
{
    public string Cpu { get; set; } = "";
    public string Gpu { get; set; } = "";
    public long RamBytes { get; set; }
    public string Motherboard { get; set; } = "";
}

/// <summary>One closed focus-session summary, mapped straight from
/// FpsSessionRecord. HistSchema pins the bucket layout FpsHistogram
/// defines; the api rejects any value other than 1.</summary>
public sealed class FpsUploadSession
{
    public string Id { get; set; } = "";
    public string GameKey { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Store { get; set; } = "";
    public int FocusedSec { get; set; }
    public int ValidSec { get; set; }
    public long Frames { get; set; }
    public int MinFps { get; set; }
    public int MaxFps { get; set; }
    public int[] Hist { get; set; } = System.Array.Empty<int>();
    public int HistSchema { get; set; } = 1;
    public int DispW { get; set; }
    public int DispH { get; set; }
    public int RefreshHz { get; set; }
    public int WinW { get; set; }
    public int WinH { get; set; }
    public bool Fullscreen { get; set; }
    public bool Capped { get; set; }
    public int CapValue { get; set; }
}
