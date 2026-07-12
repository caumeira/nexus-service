using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class ProfileSharingTests
{
    [Fact]
    public void All_ListsFiveCategoriesInDocumentedOrder()
    {
        Assert.Equal(
            new[] { ProfileSharing.Lighting, ProfileSharing.Cooling, ProfileSharing.Theme, ProfileSharing.Dashboard, ProfileSharing.Device },
            ProfileSharing.All);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("DEVICE")]
    [InlineData(" Device ")]
    public void Normalize_AcceptsDeviceCaseAndWhitespaceInsensitive(string input)
    {
        Assert.Equal(ProfileSharing.Device, ProfileSharing.Normalize(input));
    }

    [Fact]
    public void ApplyCategory_Device_ReplacesTargetStreamDeckWithSources()
    {
        var source = new NexusSettings();
        source.StreamDeck.Decks["SN-SOURCE"] = new PhysicalDeckSettings { Name = "Source Deck" };

        var target = new NexusSettings();
        target.StreamDeck.Decks["SN-STALE"] = new PhysicalDeckSettings { Name = "Stale Deck" };

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Device);

        var deck = Assert.Single(target.StreamDeck.Decks);
        Assert.Equal("SN-SOURCE", deck.Key);
        Assert.Equal("Source Deck", deck.Value.Name);
    }

    [Fact]
    public void ApplyCategory_Device_LeavesOtherCategoriesOnTargetUntouched()
    {
        var source = new NexusSettings();
        source.StreamDeck.Decks["SN-SOURCE"] = new PhysicalDeckSettings();

        var target = new NexusSettings();
        target.Lighting.LastMediaId = "keep-me";

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Device);

        Assert.Equal("keep-me", target.Lighting.LastMediaId);
    }

    [Fact]
    public void ResetCategory_Device_ClearsStreamDeckToFreshDefault()
    {
        var target = new NexusSettings();
        target.StreamDeck.Decks["SN-1"] = new PhysicalDeckSettings { Name = "Deck" };

        ProfileSharing.ResetCategory(target, ProfileSharing.Device);

        Assert.Empty(target.StreamDeck.Decks);
    }
}
