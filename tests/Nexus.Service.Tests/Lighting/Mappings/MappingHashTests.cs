using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Tests.Lighting.Mappings;

/// <summary>
/// Canonical content hashing is a cross-language contract with nexus-api's
/// artifact-hash.ts: the flat-string construction here is pinned and any
/// change to it must be mirrored there (and vice versa).
/// </summary>
public class MappingHashTests
{
    private static MappingArtifact Vector() => new()
    {
        Name = "vector",
        Device = new MappingDeviceInfo { Key = "usb:1b1c:0c1a" },
        Zones = new()
        {
            new MappingZone
            {
                ZoneIndex = 0,
                LedCount = 4,
                AspectRatio = 1f,
                Leds = new()
                {
                    new MappingLed { I = 0, U = 0f, V = 0f },
                    new MappingLed { I = 1, U = 0.3333f, V = 0.5f },
                    new MappingLed { I = 2, U = 0.6667f, V = 0.5f },
                    new MappingLed { I = 3, U = 1f, V = 1f },
                },
                Disabled = new() { 3 },
                Groups = new()
                {
                    new MappingGroup
                    {
                        Name = "Ring",
                        Ranges = new() { new MappingLedRange { Start = 0, End = 3 } },
                    },
                },
            },
        },
    };

    [Fact]
    public void CanonicalString_matches_pinned_contract()
    {
        var canonical = MappingHash.CanonicalString(Vector(), uvDecimals: 4, aspectDecimals: 4, includeGroups: true);
        Assert.Equal(
            "nexusmap:v1|z0|c4|a1.0000"
            + "|L0:0.0000:0.0000,1:0.3333:0.5000,2:0.6667:0.5000,3:1.0000:1.0000"
            + "|D3|GRing=0-3",
            canonical);
    }

    [Fact]
    public void ClusterString_drops_groups_and_coarsens()
    {
        var canonical = MappingHash.CanonicalString(Vector(), uvDecimals: 2, aspectDecimals: 1, includeGroups: false);
        Assert.Equal(
            "nexusmap:v1|z0|c4|a1.0"
            + "|L0:0.00:0.00,1:0.33:0.50,2:0.67:0.50,3:1.00:1.00"
            + "|D3",
            canonical);
    }

    [Fact]
    public void ContentHash_is_lowercase_sha256_hex()
    {
        var hash = MappingHash.ContentHash(Vector());
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Equal(hash, MappingHash.ContentHash(Vector()));
    }

    /// <summary>Shared vector pinned in nexus-api's artifact-hash.spec.ts; a mismatch means the cross-language contract drifted.</summary>
    [Fact]
    public void ContentHash_matches_registry_implementation()
        => Assert.Equal(
            "d157a6a9586f2d744244d1b7b337826b98ee00cb014c79cdf3fc0ef15579d004",
            MappingHash.ContentHash(Vector()));

    [Fact]
    public void Hash_is_invariant_to_input_ordering()
    {
        var shuffled = Vector();
        shuffled.Zones[0].Leds.Reverse();
        shuffled.Zones[0].Disabled.Reverse();
        shuffled.Zones[0].Groups.Reverse();
        Assert.Equal(MappingHash.ContentHash(Vector()), MappingHash.ContentHash(shuffled));
    }

    [Fact]
    public void ContentHash_changes_when_groups_change()
    {
        var renamed = Vector();
        renamed.Zones[0].Groups[0].Name = "Fan";
        Assert.NotEqual(MappingHash.ContentHash(Vector()), MappingHash.ContentHash(renamed));
    }

    [Fact]
    public void ClusterHash_ignores_groups_and_subcentesimal_jitter()
    {
        var jittered = Vector();
        jittered.Zones[0].Leds[1].U = 0.3349f;
        jittered.Zones[0].Groups[0].Name = "Renamed";
        Assert.Equal(MappingHash.ClusterHash(Vector()), MappingHash.ClusterHash(jittered));
        Assert.NotEqual(MappingHash.ContentHash(Vector()), MappingHash.ContentHash(jittered));
    }

    [Fact]
    public void Uv_values_are_clamped_before_formatting()
    {
        var outOfRange = Vector();
        outOfRange.Zones[0].Leds[0].U = -0.5f;
        outOfRange.Zones[0].Leds[3].V = 1.5f;
        var clamped = Vector();
        clamped.Zones[0].Leds[0].U = 0f;
        clamped.Zones[0].Leds[3].V = 1f;
        Assert.Equal(MappingHash.ContentHash(clamped), MappingHash.ContentHash(outOfRange));
    }

    [Fact]
    public void Null_count_and_aspect_serialize_as_sentinel()
    {
        var artifact = Vector();
        artifact.Zones[0].LedCount = null;
        artifact.Zones[0].AspectRatio = null;
        var canonical = MappingHash.CanonicalString(artifact, 4, 4, includeGroups: true);
        Assert.StartsWith("nexusmap:v1|z0|c-1|a-1|L", canonical);
    }
}
