using System;
using System.IO;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class LinuxProcessIconProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-process-icons-" + Guid.NewGuid().ToString("N"));

    public LinuxProcessIconProviderTests()
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

    private void WriteDesktopFile(string name, string content)
        => File.WriteAllText(Path.Combine(_root, name), content);

    [Theory]
    [InlineData("/usr/bin/firefox %u", "firefox")]
    [InlineData("firefox %u", "firefox")]
    [InlineData("gimp", "gimp")]
    [InlineData("\"/opt/My App/launcher\" %F", "launcher")]
    [InlineData("  /usr/bin/code  --unity-launch %F  ", "code")]
    [InlineData("", null)]
    [InlineData("env FOO=1 /usr/bin/real-app", null)]
    [InlineData("/usr/bin/env python3 /opt/app/main.py", null)]
    [InlineData("sh -c 'real-app --flag'", null)]
    [InlineData("/bin/bash -c real-app", null)]
    [InlineData("flatpak run org.example.App", null)]
    public void ParseExecBasename_ExtractsTheLaunchedBinary(string execValue, string? expected)
    {
        Assert.Equal(expected, LinuxProcessIconProvider.ParseExecBasename(execValue));
    }

    [Fact]
    public void BuildExecIconMap_MapsExecBasenameToIcon()
    {
        WriteDesktopFile("firefox.desktop", """
            [Desktop Entry]
            Type=Application
            Name=Firefox
            Exec=/usr/bin/firefox %u
            Icon=firefox
            """);

        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { _root });

        Assert.Equal("firefox", map["firefox"]);
    }

    [Fact]
    public void BuildExecIconMap_SkipsEntriesMissingExecOrIcon()
    {
        WriteDesktopFile("no-icon.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/noicon
            """);
        WriteDesktopFile("no-exec.desktop", """
            [Desktop Entry]
            Type=Application
            Icon=noexec
            """);

        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { _root });

        Assert.Empty(map);
    }

    [Fact]
    public void BuildExecIconMap_SkipsAWrapperExecInsteadOfMappingTheWrapperName()
    {
        WriteDesktopFile("wrapped.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=env FOO=1 /usr/bin/real-app
            Icon=real-app-icon
            """);

        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { _root });

        Assert.Empty(map);
    }

    [Fact]
    public void BuildExecIconMap_IgnoresExecInADesktopActionSection()
    {
        WriteDesktopFile("app.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/mainapp
            Icon=mainapp-icon

            [Desktop Action new-window]
            Exec=/usr/bin/mainapp --new-window
            Icon=wrong-icon
            """);

        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { _root });

        Assert.Single(map);
        Assert.Equal("mainapp-icon", map["mainapp"]);
    }

    [Fact]
    public void BuildExecIconMap_FirstDirWins_OnADuplicateBasename()
    {
        var secondDir = Path.Combine(_root, "second");
        Directory.CreateDirectory(secondDir);
        WriteDesktopFile("app.desktop", """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/app
            Icon=preferred
            """);
        File.WriteAllText(Path.Combine(secondDir, "app.desktop"), """
            [Desktop Entry]
            Type=Application
            Exec=/usr/bin/app
            Icon=overridden
            """);

        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { _root, secondDir });

        Assert.Equal("preferred", map["app"]);
    }

    [Fact]
    public void BuildExecIconMap_IncludesNoDisplayAndHiddenEntries()
    {
        // Unlike LinuxShortcutsProvider's launcher list, a running process can
        // back a helper .desktop entry a launcher would never show.
        WriteDesktopFile("helper.desktop", """
            [Desktop Entry]
            Type=Application
            NoDisplay=true
            Exec=/usr/lib/app/helper
            Icon=helper-icon
            """);

        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { _root });

        Assert.Equal("helper-icon", map["helper"]);
    }

    [Fact]
    public void BuildExecIconMap_SkipsMissingDirectoriesSilently()
    {
        var map = LinuxProcessIconProvider.BuildExecIconMap(new[] { Path.Combine(_root, "does-not-exist") });

        Assert.Empty(map);
    }

    [Fact]
    public void GetIcon_ForAnUnresolvableExe_ReturnsEmptyNeverNull()
    {
        // On a non-Linux runner this hits the platform guard; on Linux it
        // finds no matching .desktop Exec= for a path this specific. Either
        // way the contract is the same: empty bytes, never null.
        var provider = new LinuxProcessIconProvider();

        var icon = provider.GetIcon("/nexus-tests/does-not-exist-anywhere/binary");

        Assert.NotNull(icon);
        Assert.Empty(icon!);
    }
}
