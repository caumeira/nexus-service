using System.Collections.Generic;

namespace Nexus.Service.Mcp.Assistant;

/// <summary>One model the assistant picker offers. Sizes are approximate (Ollama
/// library listing), shown to the user before they commit to a download.</summary>
public sealed class AssistantCatalogModel
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public long DownloadBytes { get; init; }
    public string RamHint { get; init; } = "";
    public bool Recommended { get; init; }
}

/// <summary>
/// Fixed catalog of assistant models. All Qwen3.5 sizes carry Ollama's "tools" and
/// "thinking" capabilities, so any of them can drive the MCP tool-calling loop -
/// the picker exists to trade download/RAM cost against quality, not capability.
/// </summary>
public static class AssistantModelCatalog
{
    public const string DefaultModel = "qwen3.5:4b";

    public static readonly IReadOnlyList<AssistantCatalogModel> Models = new[]
    {
        new AssistantCatalogModel
        {
            Id = "qwen3.5:0.8b",
            Label = "Qwen3.5 0.8B",
            DownloadBytes = 1_000_000_000,
            RamHint = "~1-2 GB RAM",
        },
        new AssistantCatalogModel
        {
            Id = "qwen3.5:2b",
            Label = "Qwen3.5 2B",
            DownloadBytes = 2_700_000_000,
            RamHint = "~3 GB RAM",
        },
        new AssistantCatalogModel
        {
            Id = DefaultModel,
            Label = "Qwen3.5 4B",
            DownloadBytes = 3_400_000_000,
            RamHint = "~4-5 GB RAM",
            Recommended = true,
        },
        new AssistantCatalogModel
        {
            Id = "qwen3.5:9b",
            Label = "Qwen3.5 9B",
            DownloadBytes = 6_600_000_000,
            RamHint = "~7 GB RAM",
        },
    };

    public static bool IsKnownModel(string id)
    {
        foreach (var m in Models)
        {
            if (string.Equals(m.Id, id, System.StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
