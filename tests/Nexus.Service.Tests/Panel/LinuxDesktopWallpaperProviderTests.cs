using System;
using System.IO;
using System.Linq;
using Nexus.Service.Panel;
using Nexus.Service.Tests;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class LinuxDesktopWallpaperProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-wallpaper-safety-" + Guid.NewGuid().ToString("N"));

    public LinuxDesktopWallpaperProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("'file:///home/user/Pictures/photo.jpg'", "/home/user/Pictures/photo.jpg")]
    [InlineData("file:///home/user/Pictures/photo.jpg", "/home/user/Pictures/photo.jpg")]
    [InlineData("'file:///home/user/My%20Photo.jpg'", "/home/user/My Photo.jpg")]
    [InlineData("''", null)]
    [InlineData("", null)]
    [InlineData("'not-a-uri'", null)]
    public void FileUriToPath_UnquotesAndResolvesFileUris(string raw, string? expected)
    {
        Assert.Equal(expected, LinuxDesktopWallpaperProvider.FileUriToPath(raw));
    }

    [Theory]
    [InlineData(null, "picture-uri", "picture-uri-dark")]
    [InlineData("'default'", "picture-uri", "picture-uri-dark")]
    [InlineData("'prefer-dark'", "picture-uri-dark", "picture-uri")]
    [InlineData("'prefer-light'", "picture-uri", "picture-uri-dark")]
    public void ResolveKeyOrder_PrefersTheActiveColorScheme(string? scheme, string expectedPrimary, string expectedFallback)
    {
        var (primary, fallback) = LinuxDesktopWallpaperProvider.ResolveKeyOrder(scheme);

        Assert.Equal(expectedPrimary, primary);
        Assert.Equal(expectedFallback, fallback);
    }

    [Fact]
    public void ParseKdeImageValues_YieldsEveryImageLineInFileOrder()
    {
        var lines = new[]
        {
            "[Containments][1][Wallpaper][org.kde.image][General]",
            "Image=file:///usr/share/wallpapers/First/contents/images/1920x1080.jpg",
            "SlideInterval=300",
            "[Containments][2][Wallpaper][org.kde.image][General]",
            "  Image=file:///usr/share/wallpapers/Second/contents/images/1920x1080.jpg  ",
        };

        var values = LinuxDesktopWallpaperProvider.ParseKdeImageValues(lines).ToList();

        Assert.Equal(new[]
        {
            "file:///usr/share/wallpapers/First/contents/images/1920x1080.jpg",
            "file:///usr/share/wallpapers/Second/contents/images/1920x1080.jpg",
        }, values);
    }

    [Fact]
    public void ParseKdeImageValues_YieldsNothingWhenNoImageKeyIsPresent()
    {
        var lines = new[] { "[General]", "SomeOtherKey=value" };

        Assert.Empty(LinuxDesktopWallpaperProvider.ParseKdeImageValues(lines));
    }

    [Fact]
    public void IsServedFile_MatchesOnlyTheDconfUserDatabase()
    {
        Assert.True(LinuxDesktopWallpaperProvider.IsServedFile("user"));
        Assert.True(LinuxDesktopWallpaperProvider.IsServedFile("/home/user/.config/dconf/user"));
        Assert.False(LinuxDesktopWallpaperProvider.IsServedFile("user.lock"));
        Assert.False(LinuxDesktopWallpaperProvider.IsServedFile(null));
    }

    [Fact]
    public void PassesSafetyGates_RejectsAnExtensionOutsideTheRecognizedImageSet()
    {
        var path = Path.Combine(_root, "secret.txt");
        File.WriteAllText(path, "not an image");

        Assert.False(LinuxDesktopWallpaperProvider.PassesSafetyGates(path));
    }

    [Fact]
    public void PassesSafetyGates_AcceptsAWorldReadableImageFile()
    {
        var path = Path.Combine(_root, "wallpaper.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        Assert.True(LinuxDesktopWallpaperProvider.PassesSafetyGates(path));
    }

    [Fact]
    public void PassesSafetyGates_AcceptsAPathUnderATrustedSystemWallpaperDir()
    {
        // A prefix match on the already-resolved path, not a filesystem check.
        Assert.True(LinuxDesktopWallpaperProvider.PassesSafetyGates("/usr/share/backgrounds/default.jpg"));
        Assert.True(LinuxDesktopWallpaperProvider.PassesSafetyGates("/usr/share/wallpapers/Next/contents/images/1920x1080.png"));
    }

    [Fact]
    public void PassesSafetyGates_FallsBackToTrue_WhenNotRunningAsAnAdoptedRootDaemon()
    {
        // LinuxSession.SessionUid is only set after AdoptActiveSessionEnv runs
        // as a root daemon, which this test process never does, so a
        // non-world-readable file outside the trusted dirs still passes: the
        // owner-uid rejection path is exercisable only on a real root-daemon
        // deployment (Linux VM/lab box), not from this unit test.
        var path = Path.Combine(_root, "private.png");
        File.WriteAllBytes(path, new byte[] { 1 });
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Assert.True(LinuxDesktopWallpaperProvider.PassesSafetyGates(path));
    }

    [Fact]
    public void ResolveRealPath_CollapsesTraversalSegments()
    {
        var nested = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(nested);
        var target = Path.Combine(nested, "photo.png");
        File.WriteAllBytes(target, new byte[] { 1 });
        var traversed = Path.Combine(_root, "a", "b", "..", "b", "..", "..", "a", "b", "photo.png");

        var resolved = LinuxDesktopWallpaperProvider.ResolveRealPath(traversed);

        Assert.Equal(Path.GetFullPath(target), resolved);
        Assert.DoesNotContain("..", resolved);
    }

    [Fact]
    public void ResolveRealPath_ReturnsNull_ForAPathThatDoesNotExist()
    {
        var missing = Path.Combine(_root, "nope.png");

        Assert.Null(LinuxDesktopWallpaperProvider.ResolveRealPath(missing));
    }

    [NonWindowsFact]
    public void ResolveRealPath_FollowsASymlinkToItsFinalTarget()
    {
        var target = Path.Combine(_root, "real.png");
        File.WriteAllBytes(target, new byte[] { 1 });
        var link = Path.Combine(_root, "link.png");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(Path.GetFullPath(target), LinuxDesktopWallpaperProvider.ResolveRealPath(link));
    }

    [NonWindowsFact]
    public void ResolveRealPath_FollowsAChainOfSymlinks()
    {
        var target = Path.Combine(_root, "real.png");
        File.WriteAllBytes(target, new byte[] { 1 });
        var middle = Path.Combine(_root, "middle.png");
        File.CreateSymbolicLink(middle, target);
        var outer = Path.Combine(_root, "outer.png");
        File.CreateSymbolicLink(outer, middle);

        Assert.Equal(Path.GetFullPath(target), LinuxDesktopWallpaperProvider.ResolveRealPath(outer));
    }

    [NonWindowsFact]
    public void ResolveSafePath_RejectsASymlinkNamedLikeAnImage_WhoseTargetIsNot()
    {
        // A gsettings/KDE value fully controls the file name (including its
        // extension) but not what it links to - gating on the symlink's own
        // name instead of its resolved target would let this through.
        var target = Path.Combine(_root, "secret.txt");
        File.WriteAllText(target, "not an image");
        var link = Path.Combine(_root, "wallpaper.png");
        File.CreateSymbolicLink(link, target);

        Assert.Null(LinuxDesktopWallpaperProvider.ResolveSafePath(link));
    }

    [NonWindowsFact]
    public void ResolveSafePath_AcceptsAWorldReadableImageReachedThroughASymlink()
    {
        var target = Path.Combine(_root, "target.png");
        File.WriteAllBytes(target, new byte[] { 1 });
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        var link = Path.Combine(_root, "link.png");
        File.CreateSymbolicLink(link, target);

        Assert.Equal(Path.GetFullPath(target), LinuxDesktopWallpaperProvider.ResolveSafePath(link));
    }
}
