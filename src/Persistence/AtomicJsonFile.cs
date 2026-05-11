using System.IO;

namespace Qos.Service.Persistence;

/// <summary>
/// Crash-safe text/JSON writer. Writes the payload to "&lt;path&gt;.tmp",
/// fsyncs, then atomically renames it onto the target. If the target already
/// exists, uses File.Replace (preserves ACLs on Windows). If not, falls back
/// to File.Move.
///
/// Why not just File.WriteAllText: a power loss / crash mid-write leaves a
/// truncated/empty target. Several settings.json bugs in the past traced
/// back to that. Use this for any JSON file we read on startup.
/// </summary>
public static class AtomicJsonFile
{
    public static void Write(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        ReplaceOrMove(tmp, path);
    }

    public static void Write(string path, byte[] contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, contents);
        ReplaceOrMove(tmp, path);
    }

    private static void ReplaceOrMove(string tmp, string path)
    {
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }
}
