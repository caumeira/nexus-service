using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Service.Tests.Migration;

/// <summary>
/// SimulatedNexus2 only compiles under DEV_TOOLS, which this test config never
/// defines, so its embedded config is pinned by parsing the SOURCE text: the
/// raw string must stay valid JSON and stay content-identical to the
/// Nexus2MigrationServiceTests fixture (modulo the source-dir placeholder),
/// or the sim quietly drifts from the config shape the translators are
/// tested against.
/// </summary>
public class SimulatedNexus2FixtureTests
{
    [Fact]
    public void Embedded_config_parses_and_matches_the_test_fixture()
    {
        var repoRoot = FindRepoRoot();
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "Migration", "SimulatedNexus2.cs"));

        const string open = "ConfigJson = \"\"\"";
        var start = source.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "ConfigJson raw string not found in SimulatedNexus2.cs");
        start += open.Length;
        var end = source.IndexOf("\"\"\"", start, StringComparison.Ordinal);
        Assert.True(end > start, "ConfigJson raw string not terminated");
        var embedded = source[start..end];

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nexus2", "nexus2-config.json");
        var fixture = File.ReadAllText(fixturePath);

        var embeddedNode = JsonNode.Parse(embedded.Replace("__SIM_DIR__", "X"));
        var fixtureNode = JsonNode.Parse(fixture.Replace("__FIXTURE_DIR__", "X"));
        Assert.True(JsonNode.DeepEquals(embeddedNode, fixtureNode),
            "SimulatedNexus2.ConfigJson has drifted from Fixtures/Nexus2/nexus2-config.json");
    }

    [Fact]
    public void Sim_dir_substitution_survives_json_escaping()
    {
        // Mirrors SimulatedNexus2ConfigReader: a Windows path lands inside a
        // JSON string only via JsonEncodedText, so backslashes cannot break
        // the document.
        var dir = "C:\\Users\\someone\\App Data\\Nexus\\sim-nexus2";
        var encoded = JsonEncodedText.Encode(dir).ToString();
        using var doc = JsonDocument.Parse($"{{\"path\":\"{encoded}\"}}");
        Assert.Equal(dir, doc.RootElement.GetProperty("path").GetString());
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
