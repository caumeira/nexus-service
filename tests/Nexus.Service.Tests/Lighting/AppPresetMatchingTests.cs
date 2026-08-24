using Nexus.Service.Lighting;

namespace Nexus.Service.Tests.Lighting;

public class AppPresetMatchingTests
{
    [Theory]
    [InlineData("chrome.exe", "chrome")]
    [InlineData("Chrome.EXE", "chrome")]
    [InlineData("  Code  ", "code")]
    [InlineData("", "")]
    public void ProcessKey_lowercases_and_drops_the_exe_suffix(string raw, string expected)
        => Assert.Equal(expected, AppPresetMatching.ProcessKey(raw));

    [Fact]
    public void DisplayKey_keeps_only_letters_and_digits()
        => Assert.Equal("googlechrome", AppPresetMatching.DisplayKey("Google Chrome!"));

    [Fact]
    public void Resolved_process_name_matches_exactly()
    {
        Assert.True(AppPresetMatching.Matches("chrome", "Google Chrome", "chrome.exe"));
        Assert.False(AppPresetMatching.Matches("chrome", "Google Chrome", "chromium"));
    }

    [Fact]
    public void Resolved_process_name_wins_over_the_display_name()
    {
        // "notepad" appears in the display name but the binding resolved to a
        // different executable, so it must not match.
        Assert.False(AppPresetMatching.Matches("notepadpp", "Notepad++", "notepad"));
    }

    [Fact]
    public void Unresolved_binding_matches_the_display_name_by_substring()
    {
        Assert.True(AppPresetMatching.Matches("", "Google Chrome", "chrome"));
        Assert.True(AppPresetMatching.Matches("", "Visual Studio Code", "Code"));
        Assert.True(AppPresetMatching.Matches("", "Steam", "steam.exe"));
    }

    [Fact]
    public void Unresolved_binding_rejects_a_too_short_focused_name()
    {
        Assert.False(AppPresetMatching.Matches("", "Google Chrome", "gc"));
    }

    [Fact]
    public void Empty_focused_process_never_matches()
    {
        Assert.False(AppPresetMatching.Matches("chrome", "Google Chrome", ""));
        Assert.False(AppPresetMatching.Matches("", "Google Chrome", ""));
    }
}
