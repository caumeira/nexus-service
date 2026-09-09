using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Common;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Common;

public class DeviceGroupListTests
{
    private static DeviceGroup Group(string id, string name, params string[] members)
        => new() { Id = id, Name = name, Members = new List<string>(members) };

    [Fact]
    public void Sanitize_KeepsOrderMembershipAndName()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("g1", "  Desk  ", "openrgb-2", "keeb:tkl-1"),
            Group("g2", "Case", "np50:AABB:1"),
        });

        Assert.Equal(new[] { "g1", "g2" }, groups.Select(g => g.Id));
        Assert.Equal("Desk", groups[0].Name);
        Assert.Equal(new[] { "openrgb-2", "keeb:tkl-1" }, groups[0].Members);
    }

    [Fact]
    public void Sanitize_DropsEverythingPastTheCap()
    {
        var many = Enumerable.Range(0, DeviceGroupList.MaxGroups + 4)
            .Select(i => Group($"g{i}", $"G{i}", $"card-{i}"))
            .ToArray();

        var groups = DeviceGroupList.Sanitize(many);

        Assert.Equal(DeviceGroupList.MaxGroups, groups.Count);
        Assert.Equal("g0", groups[0].Id);
    }

    [Fact]
    public void Sanitize_LeavesAMemberInTheFirstGroupThatClaimsIt()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("g1", "Desk", "openrgb-2"),
            Group("g2", "Case", "openrgb-2", "np50:AABB:1"),
        });

        Assert.Equal(new[] { "openrgb-2" }, groups[0].Members);
        Assert.Equal(new[] { "np50:AABB:1" }, groups[1].Members);
    }

    [Fact]
    public void Sanitize_DropsAGroupLeftWithNoMembers()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("g1", "Desk", "openrgb-2"),
            Group("g2", "Empty"),
            Group("g3", "Dupes", "openrgb-2"),
        });

        Assert.Single(groups);
        Assert.Equal("g1", groups[0].Id);
    }

    [Fact]
    public void Sanitize_DropsBlankAndDuplicateGroupIds()
    {
        var groups = DeviceGroupList.Sanitize(new[]
        {
            Group("", "No id", "a"),
            Group("g1", "Desk", "b"),
            Group("g1", "Same id", "c"),
        });

        Assert.Single(groups);
        Assert.Equal("g1", groups[0].Id);
        Assert.Equal(new[] { "b" }, groups[0].Members);
    }

    [Fact]
    public void Sanitize_TrimsANameToTheFieldCap()
    {
        var groups = DeviceGroupList.Sanitize(new[] { Group("g1", new string('x', 40), "a") });

        Assert.Equal(DeviceGroupList.MaxNameLength, groups[0].Name.Length);
    }

    [Fact]
    public void Sanitize_TreatsNullAsEmpty()
    {
        Assert.Empty(DeviceGroupList.Sanitize(null));
    }
}
