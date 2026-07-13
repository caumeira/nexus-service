using System;
using System.IO;

namespace Nexus.Service.Peripherals.StreamDeck.ElgatoImport;

public enum ElgatoStoreStatus
{
    Ok,
    NotFound,
    UnsupportedVersion,
}

/// <summary>
/// Resolves the local Elgato Stream Deck software's profile store root
/// (macOS <c>~/Library/Application Support/com.elgato.StreamDeck</c>,
/// Windows <c>%APPDATA%\Elgato\StreamDeck</c>). Only ProfilesV3 (schema
/// "3.0") is supported; a ProfilesV2-only install reports UnsupportedVersion
/// so the caller can tell "no Elgato install" from "too old" apart.
/// </summary>
public sealed class ElgatoProfileLocator
{
    private const string ProfilesV3DirName = "ProfilesV3";
    private const string ProfilesV2DirName = "ProfilesV2";
    private const string RootOverrideEnvVar = "NEXUS_ELGATO_STORE_ROOT";

    private readonly string? _rootOverride;

    public ElgatoProfileLocator() : this(null)
    {
    }

    /// <summary>Test/route seam: points at a fixture dir instead of the real platform default.</summary>
    public ElgatoProfileLocator(string? rootOverride)
    {
        _rootOverride = rootOverride;
    }

    public (ElgatoStoreStatus Status, string? ProfilesV3Root) Resolve()
    {
        var elgatoDir = _rootOverride
            ?? Environment.GetEnvironmentVariable(RootOverrideEnvVar)
            ?? ResolvePlatformDefault();

        if (string.IsNullOrEmpty(elgatoDir))
        {
            return (ElgatoStoreStatus.NotFound, null);
        }

        var v3 = Path.Combine(elgatoDir, ProfilesV3DirName);
        if (Directory.Exists(v3))
        {
            return (ElgatoStoreStatus.Ok, v3);
        }

        var v2 = Path.Combine(elgatoDir, ProfilesV2DirName);
        return Directory.Exists(v2)
            ? (ElgatoStoreStatus.UnsupportedVersion, null)
            : (ElgatoStoreStatus.NotFound, null);
    }

    private static string? ResolvePlatformDefault()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "com.elgato.StreamDeck");
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Elgato", "StreamDeck");
        }
        return null;
    }
}
