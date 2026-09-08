using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Devices;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LightingDeviceNamesTests
{
    private static List<LightingDevice> Cards() => new()
    {
        new LightingDevice { Id = "openrgb-0-0", Name = "B650E - ARGB header 1", ParentDeviceId = "openrgb-0" },
        new LightingDevice { Id = "np50:AABB:1", Name = "HYTE NP50 Port 1" },
    };

    [Fact]
    public void Apply_RenamesTheCardAndKeepsTheHardwareName()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["np50:AABB:1"] = "Top intake" });

        Assert.Equal("Top intake", cards[1].Name);
        Assert.Equal("HYTE NP50 Port 1", cards[1].OriginalName);
    }

    [Fact]
    public void Apply_LeavesUnrenamedCardsWithoutAnOriginalName()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["np50:AABB:1"] = "Top intake" });

        Assert.Equal("B650E - ARGB header 1", cards[0].Name);
        Assert.Null(cards[0].OriginalName);
    }

    [Fact]
    public void Apply_IgnoresABlankStoredName()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["np50:AABB:1"] = "   " });

        Assert.Equal("HYTE NP50 Port 1", cards[1].Name);
        Assert.Null(cards[1].OriginalName);
    }

    [Fact]
    public void Apply_HandsAGroupRenameToEveryMemberCard()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["openrgb-0"] = "Motherboard" });

        Assert.Equal("Motherboard", cards[0].ParentName);
        Assert.Equal("B650E - ARGB header 1", cards[0].Name);
        Assert.Null(cards[0].OriginalName);
    }

    [Fact]
    public void Apply_LeavesParentNameUnsetOnACardWithNoRenamedGroup()
    {
        var cards = Cards();
        LightingDeviceNames.Apply(cards, new Dictionary<string, string> { ["openrgb-0-0"] = "Top strip" });

        Assert.Null(cards[0].ParentName);
        Assert.Null(cards[1].ParentName);
    }
}
