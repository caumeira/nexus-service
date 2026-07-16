using System;
using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class ConsoleUserSidMatcherTests
{
    private static (string Sid, string ProfileUsername)[] Profiles(params (string Sid, string ProfileUsername)[] entries) => entries;

    [Fact]
    public void Match_ReturnsTheExactBasenameMatch()
    {
        var profiles = Profiles(("S-1-5-21-1", "Nicola"));

        Assert.Equal("S-1-5-21-1", ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_IsCaseInsensitive_ForAnExactMatch()
    {
        var profiles = Profiles(("S-1-5-21-1", "nicola"));

        Assert.Equal("S-1-5-21-1", ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_PrefersTheExactMatch_OverASuffixedProfile()
    {
        // Local/domain same-name collision: both a plain "Nicola" and a
        // domain-suffixed "Nicola.CORP" profile exist.
        var profiles = Profiles(
            ("S-1-5-21-domain", "Nicola.CORP"),
            ("S-1-5-21-local", "Nicola"));

        Assert.Equal("S-1-5-21-local", ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_AcceptsAUniqueSuffixedProfile_WhenNoExactMatchExists()
    {
        var profiles = Profiles(("S-1-5-21-domain", "Nicola.CORP"));

        Assert.Equal("S-1-5-21-domain", ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_IsCaseInsensitive_ForASuffixedProfile()
    {
        var profiles = Profiles(("S-1-5-21-domain", "nicola.corp"));

        Assert.Equal("S-1-5-21-domain", ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_AcceptsACollisionSuffixedProfile()
    {
        // Windows suffixes a second local profile with the same folder name
        // as ".000", ".001", etc.
        var profiles = Profiles(("S-1-5-21-second", "Nicola.000"));

        Assert.Equal("S-1-5-21-second", ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_ReturnsNull_WhenMultipleSuffixedProfilesMatch()
    {
        var profiles = Profiles(
            ("S-1-5-21-a", "Nicola.CORP"),
            ("S-1-5-21-b", "Nicola.000"));

        Assert.Null(ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_ReturnsNull_WhenNoProfileMatches()
    {
        var profiles = Profiles(("S-1-5-21-1", "SomeoneElse"));

        Assert.Null(ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }

    [Fact]
    public void Match_ReturnsNull_ForAnEmptyProfileList()
    {
        Assert.Null(ConsoleUserSidMatcher.Match(Array.Empty<(string, string)>(), "Nicola"));
    }

    [Fact]
    public void Match_DoesNotMatchAProfileThatMerelyContainsTheUsername()
    {
        // "NicolaSmith" is not "Nicola" and not "Nicola.<suffix>".
        var profiles = Profiles(("S-1-5-21-1", "NicolaSmith"));

        Assert.Null(ConsoleUserSidMatcher.Match(profiles, "Nicola"));
    }
}
