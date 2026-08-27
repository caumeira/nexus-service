using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class HelperPollerDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_ForBlank(string? value)
    {
        Assert.Empty(HelperPollerDiagnostics.Parse(value));
    }

    [Fact]
    public void Parse_SplitsOnCommaSpaceAndSemicolon()
    {
        var set = HelperPollerDiagnostics.Parse("windowset, screentime;audio watchdog");
        Assert.Equal(4, set.Count);
        foreach (var known in HelperPollerDiagnostics.Known)
        {
            Assert.Contains(known, set);
        }
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var set = HelperPollerDiagnostics.Parse("WindowSet");
        Assert.Contains(HelperPollerDiagnostics.WindowSet, set);
    }

    // A typo must survive parsing so the startup log line shows it verbatim,
    // rather than reporting "all pollers enabled" for a list the user believes
    // took effect.
    [Fact]
    public void Parse_KeepsUnknownNames()
    {
        var set = HelperPollerDiagnostics.Parse("windowsett");
        Assert.Contains("windowsett", set);
        Assert.DoesNotContain(HelperPollerDiagnostics.WindowSet, set);
    }

    [Fact]
    public void FormatStartupLine_ReportsNoneDisabled()
    {
        Assert.Equal("[helper-perf] all pollers enabled",
            HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse(null)));
    }

    [Fact]
    public void FormatStartupLine_ListsDisabled()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("audio"));
        Assert.Equal("[helper-perf] disabled: audio", line);
    }

    // A typo must not read as "that poller was ruled out"; the line has to say
    // the name matched nothing and list what would have.
    [Fact]
    public void FormatStartupLine_CallsOutUnrecognizedNames()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("windowsett"));
        Assert.Contains("all pollers enabled", line);
        Assert.Contains("unrecognized, ignored: windowsett", line);
        Assert.Contains("known: windowset,screentime,audio,watchdog", line);
    }

    [Fact]
    public void FormatStartupLine_SeparatesRecognizedFromTypos()
    {
        var line = HelperPollerDiagnostics.FormatStartupLine(HelperPollerDiagnostics.Parse("audio,windowsett"));
        Assert.Contains("disabled: audio", line);
        Assert.Contains("unrecognized, ignored: windowsett", line);
    }

    // The parsed list is re-emitted into the scheduled-task XML the LocalSystem
    // service writes, so a token carrying markup must never survive parsing.
    [Theory]
    [InlineData("audio\"/><Exec>evil</Exec>")]
    [InlineData("win&dowset")]
    [InlineData("../../etc")]
    public void Parse_DropsTokensWithUnsafeCharacters(string value)
    {
        Assert.Empty(HelperPollerDiagnostics.Parse(value));
    }

    [Fact]
    public void Parse_KeepsSafeTokenWhenAnUnsafeOneIsPresent()
    {
        var set = HelperPollerDiagnostics.Parse("audio,<script>");
        Assert.Single(set);
        Assert.Contains(HelperPollerDiagnostics.Audio, set);
    }

    [Fact]
    public void FormatSlowPass_CarriesPollerDurationAndCount()
    {
        Assert.Equal("[helper-perf] windowset pass=412.5ms items=317",
            HelperPollerDiagnostics.FormatSlowPass(HelperPollerDiagnostics.WindowSet, 412.53, 317));
    }

    [Fact]
    public void Configure_ThenIsDisabled_MatchesList()
    {
        try
        {
            HelperPollerDiagnostics.Configure("windowset");
            Assert.True(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.WindowSet));
            Assert.False(HelperPollerDiagnostics.IsDisabled(HelperPollerDiagnostics.Audio));
        }
        finally
        {
            HelperPollerDiagnostics.Configure(null);
        }
    }
}
