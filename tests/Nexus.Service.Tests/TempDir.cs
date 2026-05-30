using System;
using System.IO;

namespace Nexus.Service.Tests;

/// <summary>
/// Disposable temp directory for building fake sysfs/dev trees in Linux-provider
/// tests. Shared by the serial-discovery, fan-control, and backlight tests so
/// they don't each re-roll temp-tree plumbing.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Root);

    /// <summary>Write a file at <paramref name="relPath"/> (creating parents); returns its absolute path.</summary>
    public string Write(string relPath, string content)
    {
        var p = Path.Combine(Root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    /// <summary>Create a directory at <paramref name="relPath"/>; returns its absolute path.</summary>
    public string Dir(string relPath)
    {
        var p = Path.Combine(Root, relPath);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Create a symlink at <paramref name="linkRelPath"/> pointing at absolute <paramref name="target"/>.</summary>
    public void Symlink(string linkRelPath, string target)
    {
        var p = Path.Combine(Root, linkRelPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        Directory.CreateSymbolicLink(p, target);
    }

    /// <summary>Absolute path for a relative path inside the temp dir (no creation).</summary>
    public string At(string relPath) => System.IO.Path.Combine(Root, relPath);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
