using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Mcp.Tools;

/// <summary>Write tool: the same ProfileManager.SwitchProfile POST /profiles/{id}/switch calls.</summary>
public sealed class ApplyProfileTool : IMcpTool
{
    private readonly ProfileManager _profiles;
    private readonly MultiplexHub _hub;

    public ApplyProfileTool(ProfileManager profiles, MultiplexHub hub)
    {
        _profiles = profiles;
        _hub = hub;
    }

    public string Name => "apply_profile";
    public string Title => "Apply Profile";

    public string Description =>
        "Switches the active settings profile - fans, lighting, and every other per-profile setting " +
        "change together. Use when the user asks to switch to, load, or activate a named profile. " +
        "Accepts either the profile name or its id from list_profiles.";

    public McpCapability Capability => McpCapability.Profiles;
    public bool ReadOnly => false;

    public string InputSchemaJson =>
        "{\"type\":\"object\",\"properties\":{" +
        "\"profile\":{\"type\":\"string\",\"description\":\"Profile name or id from list_profiles.\"}" +
        "},\"required\":[\"profile\"],\"additionalProperties\":false}";

    public Task<McpToolExecutionResult> ExecuteAsync(JsonElement? args, CancellationToken ct)
    {
        var requested = McpArgs.StringArg(args, "profile");
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Task.FromResult(McpToolExecutionResult.Error(
                "'profile' is required: a profile name or id from list_profiles."));
        }

        var manifest = _profiles.GetManifest();
        var entry = manifest.Profiles.FirstOrDefault(p => string.Equals(p.Id, requested, StringComparison.Ordinal))
            ?? manifest.Profiles.FirstOrDefault(p => string.Equals(p.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            var known = manifest.Profiles.Count == 0
                ? "(none)"
                : string.Join(", ", manifest.Profiles.Select(p => $"{p.Name} ({p.Id})"));
            return Task.FromResult(McpToolExecutionResult.Error($"Unknown profile '{requested}'. Known profiles: {known}."));
        }

        _profiles.SwitchProfile(entry.Id);
        PanelTopics.BroadcastPrefs(_hub);
        PanelTopics.BroadcastLighting(_hub);
        PanelTopics.BroadcastCooling(_hub);

        var result = new McpApplyProfileResult { Switched = entry.Id, Name = entry.Name };
        var json = JsonSerializer.Serialize(result, AppJsonContext.Default.McpApplyProfileResult);
        return Task.FromResult(McpToolExecutionResult.Ok(json));
    }
}
