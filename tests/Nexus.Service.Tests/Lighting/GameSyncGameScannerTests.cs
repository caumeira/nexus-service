using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Lighting;

namespace Nexus.Service.Tests.Lighting;

public class GameSyncGameScannerTests
{
    [Fact]
    public void EmitsChroma_BundledHdll_WithUtf16ChromaString_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var content = Encoding.Unicode.GetBytes("prefix RzChromaSDK.dll suffix");
            File.WriteAllBytes(Path.Combine(dir, "chroma.hdll"), content);

            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out _, out _);

            Assert.True(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmitsChroma_BundledChromaDll_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "RzChromaSDK64.dll"), Array.Empty<byte>());

            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out _, out _);

            Assert.True(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmitsChroma_BundledCChromaEditorDll_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "CChromaEditorLibrary64.dll"), Array.Empty<byte>());

            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out _, out _);

            Assert.True(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmitsChroma_AsciiChromaString_InExe_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var content = Encoding.ASCII.GetBytes("HEADER ChromaSDK FOOTER");
            File.WriteAllBytes(Path.Combine(dir, "game.exe"), content);

            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out _, out _);

            Assert.True(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmitsChroma_NoChromaEvidence_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var content = Encoding.ASCII.GetBytes("just some random bytes no chroma here");
            File.WriteAllBytes(Path.Combine(dir, "game.exe"), content);

            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out _, out _);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmitsChroma_EmptyDir_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out _, out _);

            Assert.False(result);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void EmitsChroma_PakFileWithChromaAscii_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var content = Encoding.ASCII.GetBytes("PAK_HEADER CChromaEditor PAK_FOOTER");
            File.WriteAllBytes(Path.Combine(dir, "assets.pak"), content);

            var result = GameSyncGameScanner.EmitsChroma(dir, NullLogger.Instance, out var scanned, out _);

            Assert.True(result);
            Assert.True(scanned > 0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
