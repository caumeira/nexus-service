using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Games;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Lighting;

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

    [Fact]
    public void DedupeByInstallDir_SameDir_CollapsesToOne()
    {
        var dir = @"C:\Games\Cyberpunk2077";
        var candidates = new List<(string, string, string, string)>
        {
            ("Cyberpunk 2077", dir, "epic", ""),
            ("Cyberpunk 2077", dir, "steam", ""),
        };

        var result = InstalledGameCollectors.DedupeByInstallDir(candidates);

        Assert.Single(result);
        Assert.Equal("steam", result[0].Store);
    }

    [Fact]
    public void DedupeByInstallDir_SameDirCaseInsensitive_CollapsesToOne()
    {
        var candidates = new List<(string, string, string, string)>
        {
            ("Dead Cells", @"C:\Games\DeadCells", "ubisoft", ""),
            ("Dead Cells", @"C:\games\deadcells", "steam", ""),
        };

        var result = InstalledGameCollectors.DedupeByInstallDir(candidates);

        Assert.Single(result);
        Assert.Equal("steam", result[0].Store);
    }

    [Theory]
    [InlineData("steam", "epic", "steam")]
    [InlineData("steam", "ubisoft", "steam")]
    [InlineData("epic", "ubisoft", "epic")]
    [InlineData("epic", "steam", "steam")]
    [InlineData("ubisoft", "steam", "steam")]
    [InlineData("ubisoft", "epic", "epic")]
    public void DedupeByInstallDir_StorePrecedence_HigherPrecedenceWins(
        string storeA, string storeB, string expectedWinner)
    {
        var dir = @"C:\Games\SomeGame";
        var candidates = new List<(string, string, string, string)>
        {
            ("Game", dir, storeA, ""),
            ("Game", dir, storeB, ""),
        };

        var result = InstalledGameCollectors.DedupeByInstallDir(candidates);

        Assert.Single(result);
        Assert.Equal(expectedWinner, result[0].Store);
    }

    [Fact]
    public void DedupeByInstallDir_DifferentDirs_BothKept()
    {
        var candidates = new List<(string, string, string, string)>
        {
            ("Game A", @"C:\Games\GameA", "steam", ""),
            ("Game B", @"C:\Games\GameB", "epic", ""),
        };

        var result = InstalledGameCollectors.DedupeByInstallDir(candidates);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DedupeByInstallDir_SeparatorAndDriveLetterCaseDifference_CollapsesToOne()
    {
        // Steam registry SteamPath uses forward slashes + lowercase drive;
        // libraryfolders.vdf paths use backslashes + uppercase drive after Path.Combine.
        var candidates = new List<(string, string, string, string)>
        {
            ("Cyberpunk 2077", @"C:\Games\Cyberpunk2077", "epic", ""),
            ("Cyberpunk 2077", "c:/Games/Cyberpunk2077", "steam", ""),
        };

        var result = InstalledGameCollectors.DedupeByInstallDir(candidates);

        Assert.Single(result);
        Assert.Equal("steam", result[0].Store);
    }

    [Fact]
    public async Task OnScanComplete_FiredAfterScan_WithResults()
    {
        var scanner = new GameSyncGameScanner(NullLogger<GameSyncGameScanner>.Instance, TempCachePath());
        IReadOnlyList<DetectedGame>? received = null;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        scanner.OnScanComplete = games =>
        {
            received = games;
            tcs.TrySetResult(true);
        };

        scanner.RequestScan();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(received);
        Assert.Same(scanner.Games, received);
    }

    // A scanner with no override writes to the real machine cache and the next
    // one reads it back, so every test here gets its own throwaway path.
    private static string TempCachePath()
        => Path.Combine(Path.GetTempPath(), $"nexus-gs-scan-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task ACompletedScan_SurvivesARestart()
    {
        var cache = TempCachePath();
        try
        {
            var first = new GameSyncGameScanner(NullLogger<GameSyncGameScanner>.Instance, cache);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            first.OnScanComplete = _ => done.TrySetResult(true);
            first.RequestScan();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // A second instance is the next service start: it must come up with
            // the last result already in hand, not an empty list.
            var restarted = new GameSyncGameScanner(NullLogger<GameSyncGameScanner>.Instance, cache);
            Assert.Equal(first.ScannedAt, restarted.ScannedAt);
            Assert.Equal(first.Games.Count, restarted.Games.Count);
        }
        finally
        {
            if (File.Exists(cache)) File.Delete(cache);
        }
    }

    [Fact]
    public void AnUnreadableCache_LeavesTheScannerEmptyRatherThanThrowing()
    {
        var cache = TempCachePath();
        File.WriteAllText(cache, "{ not json");
        try
        {
            var scanner = new GameSyncGameScanner(NullLogger<GameSyncGameScanner>.Instance, cache);
            Assert.Null(scanner.ScannedAt);
            Assert.Empty(scanner.Games);
        }
        finally
        {
            if (File.Exists(cache)) File.Delete(cache);
        }
    }

    [Fact]
    public async Task RequestScanIfStale_WhenNeverScanned_TriggersScan()
    {
        var scanner = new GameSyncGameScanner(NullLogger<GameSyncGameScanner>.Instance, TempCachePath());
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.OnScanComplete = _ => tcs.TrySetResult(true);

        Assert.Null(scanner.ScannedAt);
        scanner.RequestScanIfStale();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(scanner.ScannedAt);
    }

    [Fact]
    public async Task RequestScanIfStale_WhenFresh_DoesNotRescan()
    {
        var scanner = new GameSyncGameScanner(NullLogger<GameSyncGameScanner>.Instance, TempCachePath());
        var count = 0;
        var firstDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.OnScanComplete = _ =>
        {
            System.Threading.Interlocked.Increment(ref count);
            firstDone.TrySetResult(true);
        };

        scanner.RequestScan();
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, count);

        // ScannedAt is now within the freshness window, so a stale-check must
        // not start a second scan.
        scanner.RequestScanIfStale();
        await Task.Delay(400);
        Assert.Equal(1, count);
    }

}
