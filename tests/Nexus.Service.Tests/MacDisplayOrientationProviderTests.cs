#if MACOS
using System;
using System.IO;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacDisplayOrientationProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-rotate-" + Guid.NewGuid().ToString("N"));

    public MacDisplayOrientationProviderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Stand-in helper: records its argv, prints to stderr, exits with the given code.</summary>
    private string FakeHelper(int exitCode, string stderr = "")
    {
        var path = Path.Combine(_dir, "nexus-overlay-helper");
        File.WriteAllText(path, $"#!/bin/sh\necho \"$@\" > \"{_dir}/args\"\nprintf '%s' '{stderr}' >&2\nexit {exitCode}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static MacDisplayOrientationProvider Provider(string? helper, uint? cgDisplay = 5)
        => new(_ => cgDisplay, () => helper);

    [MacOnlyFact]
    public void Success_passes_the_cg_display_and_degrees()
    {
        var helper = FakeHelper(0);
        var (ok, error) = Provider(helper).SetDisplayOrientation("mac-0e58-ed00-01010101", DisplayOrientations.Portrait, "");

        Assert.True(ok, error);
        Assert.Equal("", error);
        Assert.Equal("--rotate-display=5 --rotate-degrees=90", File.ReadAllText(Path.Combine(_dir, "args")).Trim());
    }

    [MacOnlyFact]
    public void Helper_failure_surfaces_its_stderr()
    {
        var helper = FakeHelper(1, "[overlay-helper] rotate: display 5 cannot rotate");
        var (ok, error) = Provider(helper).SetDisplayOrientation("id", DisplayOrientations.LandscapeFlipped, "");

        Assert.False(ok);
        Assert.Equal("display 5 cannot rotate", error);
    }

    [MacOnlyFact]
    public void Silent_helper_failure_reports_the_exit_code()
    {
        var (ok, error) = Provider(FakeHelper(3)).SetDisplayOrientation("id", DisplayOrientations.Landscape, "");

        Assert.False(ok);
        Assert.Equal("rotation failed (exit 3)", error);
    }

    [MacOnlyFact]
    public void Detached_display_fails_without_spawning()
    {
        var helper = FakeHelper(0);
        var (ok, error) = Provider(helper, cgDisplay: null).SetDisplayOrientation("gone", DisplayOrientations.Portrait, "");

        Assert.False(ok);
        Assert.Equal("display not attached", error);
        Assert.False(File.Exists(Path.Combine(_dir, "args")));
    }

    [MacOnlyFact]
    public void Unknown_orientation_is_rejected()
    {
        var (ok, error) = Provider(FakeHelper(0)).SetDisplayOrientation("id", "Sideways", "");

        Assert.False(ok);
        Assert.Equal("unknown orientation 'Sideways'", error);
    }

    [MacOnlyFact]
    public void Missing_helper_is_an_error()
    {
        var (ok, error) = Provider(Path.Combine(_dir, "absent")).SetDisplayOrientation("id", DisplayOrientations.Portrait, "");

        Assert.False(ok);
        Assert.Equal("overlay helper not found", error);
    }
}
#endif
