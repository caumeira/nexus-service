using System.Text.Json;
using Nexus.Service.Migration;

namespace Nexus.Service.Tests.Migration;

/// <summary>Re-parses the fixture text on every Read(), mirroring a real
/// config.json read - callers that dispose the result after each Preview/
/// Apply call (as the production service does) never hit a disposed
/// JsonDocument on a second call within the same test.</summary>
internal sealed class FakeNexus2ConfigReader : INexus2ConfigReader
{
    public string? ConfigText;
    public string ConfigDir = "";

    public Nexus2ConfigReadResult? Read() =>
        ConfigText is null ? null : new Nexus2ConfigReadResult(JsonDocument.Parse(ConfigText), ConfigDir);
}
