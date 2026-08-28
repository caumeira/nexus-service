using System.Text;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Games;

/// <summary>
/// Cheap installed-game enumeration (manifests/registry only, no file
/// content scan) shared by GameSyncGameScanner (which layers its own Chroma
/// file scan on top) and GameCatalog. Moved out of GameSyncGameScanner
/// verbatim so its own scan behavior and output are unchanged.
/// </summary>
internal static class InstalledGameCollectors
{
    public readonly record struct Candidate(string Name, string InstallDir, string Store, string AppId);

    public static List<Candidate> CollectAll(ILogger logger)
    {
        var candidates = new List<(string, string, string, string)>();
        CollectSteamGames(candidates, logger);
        CollectUbisoftGames(candidates, logger);
        CollectEpicGames(candidates, logger);
        return DedupeByInstallDir(candidates).Select(c => new Candidate(c.Name, c.InstallDir, c.Store, c.AppId)).ToList();
    }

    // Store precedence for dedupe: lower value wins.
    private static int StorePrecedence(string store) => store switch
    {
        "steam" => 0,
        "epic" => 1,
        _ => 2,
    };

    // Normalizes Windows-style paths for dedup. Path.GetFullPath unifies separators
    // on Windows; on non-Windows hosts (test runs) it won't, so we normalize slashes
    // explicitly. The dict uses OrdinalIgnoreCase so drive-letter case is handled.
    public static string CanonicalDirKey(string dir)
    {
        try
        {
            return Path.GetFullPath(dir).TrimEnd('\\', '/').Replace('/', '\\');
        }
        catch
        {
            return dir.Trim().Replace('/', '\\');
        }
    }

    // Collapse candidates sharing the same normalized installDir to one entry,
    // keeping the one from the highest-precedence store (steam > epic > ubisoft).
    internal static List<(string Name, string InstallDir, string Store, string AppId)> DedupeByInstallDir(
        List<(string Name, string InstallDir, string Store, string AppId)> candidates)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<(string Name, string InstallDir, string Store, string AppId)>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var (name, dir, store, appId) = candidates[i];
            var key = CanonicalDirKey(dir);
            if (seen.TryGetValue(key, out var existingIdx))
            {
                if (StorePrecedence(store) < StorePrecedence(result[existingIdx].Store))
                {
                    result[existingIdx] = (name, dir, store, appId);
                }
            }
            else
            {
                seen[key] = result.Count;
                result.Add((name, dir, store, appId));
            }
        }
        return result;
    }

    private static void CollectSteamGames(List<(string, string, string, string)> candidates, ILogger logger)
    {
        try
        {
            var libraryFolders = new List<string>(Nexus.Service.Lighting.SteamLibraryLocator.EnumerateLibraryPaths());
            if (libraryFolders.Count == 0)
            {
                return;
            }

            foreach (var library in libraryFolders)
            {
                var appsDir = Path.Combine(library, "steamapps");
                if (!Directory.Exists(appsDir))
                {
                    continue;
                }

                IEnumerable<string> manifests;
                try
                {
                    manifests = Directory.EnumerateFiles(appsDir, "appmanifest_*.acf");
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[game-catalog] cannot enumerate steamapps in {Dir}", appsDir);
                    continue;
                }

                foreach (var manifest in manifests)
                {
                    try
                    {
                        var manifestFileName = Path.GetFileNameWithoutExtension(manifest);
                        var underscoreIdx = manifestFileName.IndexOf('_');
                        var appId = underscoreIdx >= 0 ? manifestFileName.Substring(underscoreIdx + 1) : "";

                        var name = "";
                        var installDir = "";
                        foreach (var rawLine in File.ReadLines(manifest))
                        {
                            var line = rawLine.Trim();
                            var key = ReadFirstQuotedValue(line);
                            if (key.Equals("name", StringComparison.OrdinalIgnoreCase) && name.Length == 0)
                            {
                                name = ReadSecondQuotedValue(line);
                            }
                            else if (key.Equals("installdir", StringComparison.OrdinalIgnoreCase) && installDir.Length == 0)
                            {
                                installDir = ReadSecondQuotedValue(line);
                            }
                        }

                        if (name.Length > 0 && installDir.Length > 0)
                        {
                            var fullPath = Path.Combine(library, "steamapps", "common", installDir);
                            if (Directory.Exists(fullPath))
                            {
                                candidates.Add((name, fullPath, "steam", appId));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[game-catalog] cannot read manifest {Manifest}", manifest);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[game-catalog] Steam store unavailable");
        }
    }

    private static void CollectUbisoftGames(List<(string, string, string, string)> candidates, ILogger logger)
    {
#if WINDOWS
        try
        {
            string? gamesDir = null;
            try
            {
                var regVal = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir", null) as string;
                if (regVal is not null)
                {
                    gamesDir = Path.Combine(regVal, "games");
                }
            }
            catch
            {
                // fall through to default path
            }

            if (gamesDir is null || !Directory.Exists(gamesDir))
            {
                gamesDir = @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games";
            }

            if (!Directory.Exists(gamesDir))
            {
                return;
            }

            foreach (var dir in Directory.EnumerateDirectories(gamesDir))
            {
                var gameName = Path.GetFileName(dir);
                if (gameName.Length > 0)
                {
                    candidates.Add((gameName, dir, "ubisoft", ""));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[game-catalog] Ubisoft Connect store unavailable");
        }
#endif
    }

    private static void CollectEpicGames(List<(string, string, string, string)> candidates, ILogger logger)
    {
#if WINDOWS
        try
        {
            var manifestDir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            if (!Directory.Exists(manifestDir))
            {
                return;
            }

            foreach (var item in Directory.EnumerateFiles(manifestDir, "*.item"))
            {
                try
                {
                    var text = File.ReadAllText(item);
                    var displayName = ExtractJsonStringValue(text, "DisplayName");
                    var installLocation = ExtractJsonStringValue(text, "InstallLocation");
                    if (displayName.Length > 0 && installLocation.Length > 0 && Directory.Exists(installLocation))
                    {
                        candidates.Add((displayName, installLocation, "epic", ""));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[game-catalog] cannot read Epic manifest {Item}", item);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[game-catalog] Epic Games store unavailable");
        }
#endif
    }

    // Extracts the value of `"key":"value"` from raw JSON text without full deserialization.
    private static string ExtractJsonStringValue(string text, string key)
    {
        var search = $"\"{key}\":\"";
        var idx = text.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0)
        {
            return "";
        }

        var valueStart = idx + search.Length;
        var valueEnd = text.IndexOf('"', valueStart);
        if (valueEnd < valueStart)
        {
            return "";
        }

        return text.Substring(valueStart, valueEnd - valueStart);
    }

    private static string ReadFirstQuotedValue(string line)
    {
        var start = line.IndexOf('"');
        if (start < 0)
        {
            return "";
        }

        var end = line.IndexOf('"', start + 1);
        return end > start ? line.Substring(start + 1, end - start - 1) : "";
    }

    private static string ReadSecondQuotedValue(string line)
    {
        var first = line.IndexOf('"');
        if (first < 0)
        {
            return "";
        }

        var firstEnd = line.IndexOf('"', first + 1);
        if (firstEnd < 0)
        {
            return "";
        }

        var second = line.IndexOf('"', firstEnd + 1);
        if (second < 0)
        {
            return "";
        }

        var secondEnd = line.IndexOf('"', second + 1);
        return secondEnd > second ? line.Substring(second + 1, secondEnd - second - 1) : "";
    }
}

/// <summary>One catalog game, keyed by a store-qualified identity. gameKey is
/// the cross-repo identity ("steam:&lt;appId&gt;" | "epic:&lt;slug&gt;" |
/// "ubisoft:&lt;slug&gt;"), slug being the lowercase alnum of Name.</summary>
public sealed record GameIdentity(string GameKey, string Name, string Store, string AppId);

/// <summary>
/// Installed-game identity resolver: enumerates the same Steam/Epic/Ubisoft
/// sources GameSyncGameScanner does (via InstalledGameCollectors, so both
/// share one implementation) and matches a foreground exe path to a game by
/// longest install-dir prefix. Refreshed at boot and rate-limited to once
/// per RefreshRateLimitSeconds thereafter via NotifyUnknownExe, so a newly
/// installed game is picked up without a service restart.
/// </summary>
public sealed class GameCatalog
{
    private const long RefreshRateLimitSeconds = 600;

    private readonly ILogger<GameCatalog> _logger;
    private readonly object _lock = new();
    private long _lastRefreshEpoch;
    private IReadOnlyList<GameIdentity> _games = Array.Empty<GameIdentity>();
    private IReadOnlyList<(string DirKey, GameIdentity Game)> _resolveIndex = Array.Empty<(string, GameIdentity)>();

    public GameCatalog(ILogger<GameCatalog> logger)
    {
        _logger = logger;
        // Off the constructor thread: GameCatalog is a constructor dependency
        // of FpsSessionRecorder (a hosted service), so a synchronous scan
        // here would add Steam/Epic/Ubisoft enumeration latency directly to
        // service boot, the same reason GameSyncGameScanner's own scan runs
        // via Task.Run rather than inline.
        Task.Run(RefreshNow);
    }

    public IReadOnlyList<GameIdentity> Games => _games;

    public void RefreshNow()
    {
        var candidates = InstalledGameCollectors.CollectAll(_logger);
        var games = new List<GameIdentity>(candidates.Count);
        var index = new List<(string, GameIdentity)>(candidates.Count);
        foreach (var c in candidates)
        {
            var identity = new GameIdentity(BuildGameKey(c.Store, c.AppId, c.Name), c.Name, c.Store, c.AppId);
            games.Add(identity);
            index.Add((InstalledGameCollectors.CanonicalDirKey(c.InstallDir), identity));
        }

        lock (_lock)
        {
            _games = games;
            _resolveIndex = index;
            _lastRefreshEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    /// <summary>Rate-limited refresh trigger for a foreground exe the catalog
    /// could not resolve - a no-op within RefreshRateLimitSeconds of the last
    /// refresh, so a run of unrelated exes cannot each force a rescan.</summary>
    public void NotifyUnknownExe()
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _lastRefreshEpoch < RefreshRateLimitSeconds)
            {
                return;
            }
        }
        RefreshNow();
    }

    /// <summary>Resolves exePath to the catalog game whose install dir is the
    /// longest matching prefix (case-insensitive, separator-normalized).
    /// False when no catalog game's install dir contains exePath.</summary>
    public bool TryResolve(string exePath, out GameIdentity identity)
    {
        IReadOnlyList<(string DirKey, GameIdentity Game)> snapshot;
        lock (_lock) { snapshot = _resolveIndex; }
        return TryResolveAgainst(snapshot, exePath, out identity);
    }

    // Pure core of TryResolve, directly unit-testable against an injected
    // index rather than the live filesystem scan.
    internal static bool TryResolveAgainst(
        IReadOnlyList<(string DirKey, GameIdentity Game)> index, string exePath, out GameIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        string normalizedExe;
        try
        {
            normalizedExe = Path.GetFullPath(exePath).Replace('/', '\\');
        }
        catch
        {
            return false;
        }

        GameIdentity? best = null;
        var bestLen = -1;
        foreach (var (dirKey, game) in index)
        {
            if (dirKey.Length <= bestLen)
            {
                continue;
            }
            if (normalizedExe.StartsWith(dirKey + "\\", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedExe, dirKey, StringComparison.OrdinalIgnoreCase))
            {
                best = game;
                bestLen = dirKey.Length;
            }
        }

        if (best is null)
        {
            return false;
        }
        identity = best;
        return true;
    }

    internal static string BuildGameKey(string store, string appId, string name) => store switch
    {
        "steam" => $"steam:{appId}",
        _ => $"{store}:{Slugify(name)}",
    };

    internal static string Slugify(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }
}
