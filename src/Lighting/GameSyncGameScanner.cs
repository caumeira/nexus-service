using System.Text;
using Microsoft.Extensions.Logging;
using Nexus.Service.Models.Lighting;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Lighting;

public sealed class GameSyncGameScanner
{
    private const long MaxScanFileSizeBytes = 150L * 1024 * 1024;

    // Per-game scan budget. A Steam library with many large games can easily reach
    // tens of GB if every .pak is read; cap both axes so a single scan stays bounded.
    private const int MaxScanFilesPerGame = 400;
    private const long MaxScanBytesPerGame = 2L * 1024 * 1024 * 1024; // 2 GB

    // Streamed read buffer: avoids allocating the full file in the LOH.
    private const int StreamReadBufferSize = 65536;

    private readonly ILogger<GameSyncGameScanner> _logger;
    private readonly object _lock = new();
    private volatile bool _scanning;
    private long _scannedAtEpoch;
    private IReadOnlyList<DetectedGame> _games = Array.Empty<DetectedGame>();
    private int _scanRunning;

    public GameSyncGameScanner(ILogger<GameSyncGameScanner> logger)
    {
        _logger = logger;
    }

    public bool Scanning => _scanning;
    public long? ScannedAt => _scannedAtEpoch == 0 ? null : _scannedAtEpoch;
    public IReadOnlyList<DetectedGame> Games => _games;

    public void RequestScan(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _scanRunning, 1, 0) != 0)
        {
            return;
        }

        Task.Run(() => RunScan(ct), ct);
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
    private static string CanonicalDirKey(string dir)
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

    private void RunScan(CancellationToken ct)
    {
        _scanning = true;
        try
        {
            var candidates = new List<(string Name, string InstallDir, string Store, string AppId)>();
            CollectSteamGames(candidates);
            CollectUbisoftGames(candidates);
            CollectEpicGames(candidates);
            candidates = DedupeByInstallDir(candidates);

            var results = new List<DetectedGame>(candidates.Count);
            foreach (var (name, installDir, store, appId) in candidates)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var emits = EmitsChroma(installDir, _logger, out var scanned, out var skipped);
                results.Add(new DetectedGame
                {
                    Name = name,
                    Store = store,
                    InstallDir = installDir,
                    AppId = appId,
                    EmitsChroma = emits,
                    EmitsGsi = store == "steam" && appId == "730",
                    ScannedFiles = scanned,
                    SkippedFiles = skipped,
                });
            }

            lock (_lock)
            {
                _games = results.AsReadOnly();
                _scannedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            var emitterCount = results.Count(g => g.EmitsChroma);
            _logger.LogInformation("[game-sync-scanner] scan complete: {GameCount} games, {EmitterCount} emitters",
                results.Count, emitterCount);
        }
        finally
        {
            _scanning = false;
            Interlocked.Exchange(ref _scanRunning, 0);
        }
    }

    private void CollectSteamGames(List<(string, string, string, string)> candidates)
    {
        try
        {
            var libraryFolders = new List<string>(SteamLibraryLocator.EnumerateLibraryPaths());
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
                    _logger.LogDebug(ex, "[game-sync-scanner] cannot enumerate steamapps in {Dir}", appsDir);
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
                        _logger.LogDebug(ex, "[game-sync-scanner] cannot read manifest {Manifest}", manifest);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[game-sync-scanner] Steam store unavailable");
        }
    }

    private void CollectUbisoftGames(List<(string, string, string, string)> candidates)
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
            _logger.LogWarning(ex, "[game-sync-scanner] Ubisoft Connect store unavailable");
        }
#endif
    }

    private void CollectEpicGames(List<(string, string, string, string)> candidates)
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
                    _logger.LogDebug(ex, "[game-sync-scanner] cannot read Epic manifest {Item}", item);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[game-sync-scanner] Epic Games store unavailable");
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

    internal static bool EmitsChroma(string installDir, ILogger logger, out int scannedFiles, out int skippedFiles)
    {
        scannedFiles = 0;
        skippedFiles = 0;

        if (!Directory.Exists(installDir))
        {
            return false;
        }

        // Step 1: bundled DLL/file name check - no byte reading needed.
        foreach (var file in EnumerateFilesDepthCapped(installDir, "*", 4, logger))
        {
            var fileName = Path.GetFileName(file);
            if (IsBundledChromaFile(fileName))
            {
                return true;
            }
        }

        // Step 2: byte-scan candidate files for Chroma SDK strings.
        // Stops once either per-game budget is hit (MaxScanFilesPerGame or MaxScanBytesPerGame).
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".dll", ".hdll", ".hl", ".dat", ".pak"
        };

        long totalBytesRead = 0;

        foreach (var file in EnumerateFilesDepthCapped(installDir, "*", 4, logger))
        {
            if (scannedFiles + skippedFiles >= MaxScanFilesPerGame)
            {
                logger.LogDebug("[game-sync-scanner] file budget reached for {Dir}", installDir);
                break;
            }

            if (totalBytesRead >= MaxScanBytesPerGame)
            {
                logger.LogDebug("[game-sync-scanner] byte budget reached for {Dir}", installDir);
                break;
            }

            var ext = Path.GetExtension(file);
            if (!extensions.Contains(ext))
            {
                continue;
            }

            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[game-sync-scanner] cannot stat {File}", file);
                continue;
            }

            if (size > MaxScanFileSizeBytes)
            {
                logger.LogDebug("[game-sync-scanner] skipping large file {File} ({Size} bytes)", file, size);
                skippedFiles++;
                continue;
            }

            // Clamp to remaining byte budget so we don't read more than allowed.
            var remaining = MaxScanBytesPerGame - totalBytesRead;
            if (size > remaining)
            {
                logger.LogDebug("[game-sync-scanner] skipping {File}: would exceed byte budget", file);
                skippedFiles++;
                continue;
            }

            try
            {
                if (ContainsChromaStringStreamed(file, size))
                {
                    scannedFiles++;
                    totalBytesRead += size;
                    return true;
                }

                scannedFiles++;
                totalBytesRead += size;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[game-sync-scanner] cannot read {File}", file);
            }
        }

        return false;
    }

    // Scans a file for Chroma SDK marker strings using a fixed buffer to avoid
    // allocating the full file in the LOH. Handles search terms that may straddle
    // a buffer boundary by retaining a suffix of the previous chunk.
    private static bool ContainsChromaStringStreamed(string path, long fileSize)
    {
        // For small files, ReadAllBytes is already cheap and avoids the overlap logic.
        if (fileSize <= StreamReadBufferSize * 2)
        {
            var bytes = File.ReadAllBytes(path);
            return ContainsChromaString(bytes.AsSpan());
        }

        // Longest search term in bytes (Unicode): max term chars * 2.
        int maxTermLen = 0;
        foreach (var term in ChromaSearchTerms)
        {
            var utf16Len = term.Length * 2;
            if (utf16Len > maxTermLen)
            {
                maxTermLen = utf16Len;
            }
        }

        // Overlap keeps the tail of each chunk so terms straddling a boundary are found.
        var buf = new byte[StreamReadBufferSize + maxTermLen];
        int overlapLen = 0;

        using var fs = File.OpenRead(path);
        while (true)
        {
            int read = ReadFull(fs, buf, overlapLen, StreamReadBufferSize);
            if (read == 0)
            {
                break;
            }

            int windowLen = overlapLen + read;
            if (ContainsChromaString(buf.AsSpan(0, windowLen)))
            {
                return true;
            }

            // Retain last maxTermLen-1 bytes as overlap for the next chunk.
            int newOverlap = Math.Min(maxTermLen - 1, windowLen);
            if (newOverlap > 0)
            {
                Buffer.BlockCopy(buf, windowLen - newOverlap, buf, 0, newOverlap);
            }

            overlapLen = newOverlap;

            if (read < StreamReadBufferSize)
            {
                break;
            }
        }

        return false;
    }

    private static int ReadFull(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static bool IsBundledChromaFile(string fileName)
    {
        if (fileName.StartsWith("CChromaEditor", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("RzChroma", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.StartsWith("RzChromatic", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fileName.EndsWith(".chroma", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static readonly string[] ChromaSearchTerms = new[]
    {
        "RzChromaSDK",
        "RzChromatic",
        "ChromaSDK",
        "CChromaEditor",
    };

    private static bool ContainsChromaString(ReadOnlySpan<byte> data)
    {
        foreach (var term in ChromaSearchTerms)
        {
            var asciiBytes = Encoding.ASCII.GetBytes(term);
            if (data.IndexOf(asciiBytes.AsSpan()) >= 0)
            {
                return true;
            }

            var utf16Bytes = Encoding.Unicode.GetBytes(term);
            if (data.IndexOf(utf16Bytes.AsSpan()) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFilesDepthCapped(
        string dir, string pattern, int maxDepth, ILogger logger)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[game-sync-scanner] cannot enumerate files in {Dir}", dir);
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        if (maxDepth <= 0)
        {
            yield break;
        }

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[game-sync-scanner] cannot enumerate subdirectories in {Dir}", dir);
            yield break;
        }

        foreach (var subdir in subdirs)
        {
            foreach (var file in EnumerateFilesDepthCapped(subdir, pattern, maxDepth - 1, logger))
            {
                yield return file;
            }
        }
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
