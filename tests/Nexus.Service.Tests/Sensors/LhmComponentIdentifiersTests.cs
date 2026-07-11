using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmComponentIdentifiersTests
{
    [Theory]
    [InlineData("/memory/dimm/0", true)]
    [InlineData("/memory/dimm/1", true)]
    [InlineData("/memory/dimm0", true)]
    [InlineData("/memory/dimm", true)]
    [InlineData("/ram", false)]
    [InlineData("/vram", false)]
    [InlineData("/memory/other/0", false)]
    [InlineData("", false)]
    public void IsDimmModule_matches_only_dimm_nodes(string identifier, bool expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.IsDimmModule(identifier));
    }

    [Theory]
    [InlineData("/nvme/0", "smart/nvme/0")]
    [InlineData("/hdd/1", "smart/hdd/1")]
    public void BuildSmartStorageId_prefixes_and_strips_the_leading_slash(string identifier, string expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.BuildSmartStorageId(identifier));
    }

    [Theory]
    [InlineData("smart/nvme/0", true)]
    [InlineData("smart/hdd/1", true)]
    [InlineData("C:", false)]
    [InlineData("D:", false)]
    [InlineData("", false)]
    public void IsSmartStorageComponent_matches_only_smart_prefixed_ids(string componentId, bool expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.IsSmartStorageComponent(componentId));
    }
}
