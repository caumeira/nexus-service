using System;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Migration;

/// <summary>Parsed Nexus 2 config.json plus the directory it lives in (the
/// "HYTE Nexus" AppData root), needed to resolve the Q-series wallpaper
/// gallerySource filename against q60\web\user-media. Owns the JsonDocument;
/// callers must dispose.</summary>
public sealed class Nexus2ConfigReadResult : IDisposable
{
    public JsonDocument Document { get; }
    public string ConfigDir { get; }

    public Nexus2ConfigReadResult(JsonDocument document, string configDir)
    {
        Document = document;
        ConfigDir = configDir;
    }

    public void Dispose() => Document.Dispose();
}

/// <summary>Locates and parses a Nexus 2 install's config.json. Read-only,
/// tolerant: a missing or corrupt file returns null rather than throwing.</summary>
public interface INexus2ConfigReader
{
    Nexus2ConfigReadResult? Read();
}

#if WINDOWS
public sealed class Nexus2ConfigReader : INexus2ConfigReader
{
    private const string ConfigJsonRelativePath = @"AppData\Roaming\HYTE Nexus\config.json";

    public Nexus2ConfigReadResult? Read()
    {
        foreach (var profileDir in Nexus2ProfileDirs.Candidates())
        {
            var path = Path.Combine(profileDir, ConfigJsonRelativePath);
            if (!SafeFileExists(path) || SafeFileLength(path) == 0)
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(Nexus2ReadOnlyIo.ReadAllText(path));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                continue;
            }

            return new Nexus2ConfigReadResult(doc, Path.GetDirectoryName(path)!);
        }
        return null;
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
#else
public sealed class Nexus2ConfigReader : INexus2ConfigReader
{
    public Nexus2ConfigReadResult? Read() => null;
}
#endif
