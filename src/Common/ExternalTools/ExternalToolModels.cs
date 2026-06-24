using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Lifecycle state of an external tool process.
/// </summary>
public enum ToolStatus
{
    /// <summary>No matching hardware on the bus - nothing to run.</summary>
    NoDevice,
    /// <summary>Hardware present (or resolvable) but the tool isn't running.</summary>
    NotRunning,
    /// <summary>The tool process is alive.</summary>
    Running,
    /// <summary>Resolve / download / launch failed.</summary>
    Failed,
}

/// <summary>Which OS session the tool launches into.</summary>
public enum ToolSession
{
    /// <summary>
    /// Launch in the service's own context - LocalSystem, Session 0 - so the tool
    /// runs before any user logs in. The driver process handle is retained.
    /// </summary>
    System,
    /// <summary>
    /// Launch in the active console user's interactive session (Windows: via the
    /// scheduled-task helper). For tools that draw a UI the user must see.
    /// </summary>
    User,
}

/// <summary>Install medium a tool targets: a process on this host, or an APK
/// pushed to an adb-connected device. Selects the <see cref="IToolInstallStrategy"/>.</summary>
public enum ToolTarget
{
    HostExe,
    AndroidAdb,
}

/// <summary>Process-launch options carried in an <see cref="ExternalToolSpec"/>.</summary>
public sealed record ToolLaunchOptions(bool Hidden = true, ToolSession Session = ToolSession.System);

/// <summary>
/// Everything the <see cref="ExternalToolManager"/> needs to resolve, fetch, and
/// launch one tool variant. Built from an app's <c>driver</c> manifest block by a
/// thin per-device factory (maps a USB match to a variant).
/// </summary>
public sealed record ExternalToolSpec(
    string ToolId,
    string Variant,
    string ManifestUrl,
    string DownloadUrlBase,
    string FilePattern,
    ToolLaunchOptions Launch,
    string? PreloadDir = null,
    ToolTarget Target = ToolTarget.HostExe,
    string? Package = null);

/// <summary>
/// Remote tool manifest hosted on <c>assets.hellonexus.com</c>. Same shape as the
/// firmware manifest (latest + per-version metadata) but for fetchable executables
/// rather than flashable images. Binaries are hash-pinned: <see cref="ToolVersion.Sha256"/>
/// and <see cref="ToolVersion.Size"/> are verified on every download and cache hit.
/// </summary>
public sealed class ToolManifest
{
    [JsonPropertyName("latestVersion")]
    public string LatestVersion { get; set; } = "";

    [JsonPropertyName("versions")]
    public Dictionary<string, ToolVersion> Versions { get; set; } = new();
}

public sealed class ToolVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    /// <summary>Absolute download URL. When null, <c>DownloadUrlBase/FileName</c> is used.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Lowercase hex SHA-256 of the binary. Required - the trust pin.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Expected size in bytes. Pre-check before hashing.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Android versionCode for the APK. Used by AndroidAdbInstallStrategy to skip downgrades.</summary>
    [JsonPropertyName("versionCode")]
    public int? VersionCode { get; set; }
}

/// <summary>
/// Optional <c>bundled.json</c> pin dropped next to a preloaded binary for the
/// offline / air-gapped / OEM path. Pins a chosen file (and its hash) so the
/// manager never touches the network. The hash keeps the offline path verified.
/// </summary>
public sealed class ToolBundledPin
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
