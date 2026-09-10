using System.Linq;
using Nexus.Service.Lighting.Mappings;

namespace Nexus.Service.Tests.Lighting.Mappings;

/// <summary>
/// The built-in catalog is data, so these tests guard the two ways data goes
/// wrong: the embedded resource not being embedded at all (which degrades to
/// an empty picker with no error anywhere), and an entry that would be
/// rejected by the very lint every apply runs it through.
/// </summary>
public class BuiltInMappingsCatalogTests
{
    [Fact]
    public void Catalog_loads_from_the_embedded_resource()
    {
        // A missing LogicalName in the csproj fails exactly this way: no
        // exception, no log the user sees, just an empty picker.
        Assert.NotEmpty(BuiltInMappingsCatalog.All);
    }

    [Fact]
    public void Every_entry_has_a_product_key_and_a_unique_one()
    {
        var keys = BuiltInMappingsCatalog.All.Select(e => e.Key).ToList();
        Assert.All(keys, k => Assert.StartsWith("product:", k));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Every_artifact_passes_the_lint_that_gates_apply()
    {
        foreach (var entry in BuiltInMappingsCatalog.All)
        {
            var result = MappingLint.Validate(entry.Artifact);
            Assert.True(result.Ok,
                $"{entry.Key}: {string.Join("; ", result.Errors)}");
            // A shipped default that lint would refuse to auto-apply is a
            // packaging mistake, not a user's deliberate hidden-fan layout.
            Assert.False(result.AutoApplyIneligible, entry.Key);
        }
    }

    [Fact]
    public void Every_artifact_declares_its_key_as_its_device_key()
    {
        // Assign persists MappingId from the catalog key and the artifact
        // carries device.key independently; a drift between them would make a
        // re-assign look like a different mapping.
        foreach (var entry in BuiltInMappingsCatalog.All)
            Assert.Equal(entry.Key, entry.Artifact.Device.Key);
    }

    [Fact]
    public void Find_returns_the_artifact_for_a_known_key_and_null_otherwise()
    {
        var known = BuiltInMappingsCatalog.All[0].Key;
        Assert.NotNull(BuiltInMappingsCatalog.Find(known));
        Assert.Null(BuiltInMappingsCatalog.Find("product:not-a-real-product"));
    }

    [Fact]
    public void Search_ranks_a_name_match_above_a_brand_match()
    {
        var rows = BuiltInMappingsCatalog.Search("corsair", type: null, limit: 200);
        Assert.NotEmpty(rows);
        var firstBrandOnly = rows.FindIndex(
            r => !r.Name.Contains("corsair", System.StringComparison.OrdinalIgnoreCase));
        var lastNameMatch = rows.FindLastIndex(
            r => r.Name.Contains("corsair", System.StringComparison.OrdinalIgnoreCase));
        if (firstBrandOnly >= 0)
            Assert.True(lastNameMatch < firstBrandOnly);
    }

    [Fact]
    public void Search_filters_by_type_and_honours_the_limit()
    {
        var strips = BuiltInMappingsCatalog.Search(null, "Strip", limit: 500);
        Assert.NotEmpty(strips);
        Assert.All(strips, r => Assert.Equal("Strip", r.Type));
        Assert.Equal(3, BuiltInMappingsCatalog.Search(null, "Strip", limit: 3).Count);
    }

    [Fact]
    public void Generic_strips_and_fan_rings_are_searchable()
    {
        // The two shapes a user is most likely to have on a header, and the
        // ones SignalRGB's catalog does not carry generically at all.
        Assert.NotEmpty(BuiltInMappingsCatalog.Search("Generic ARGB Strip", null, 5));
        Assert.NotEmpty(BuiltInMappingsCatalog.Search("Generic ARGB Fan", null, 5));
    }
}
