using System.Collections.Generic;

namespace Nexus.Service.Integrations.HomeAssistant;

public sealed class HaEntityDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"light" or "switch" (domain prefix of entity_id).</summary>
    public string Domain { get; set; } = "";
    public string State { get; set; } = "";
    public bool On { get; set; }
    public bool Reachable { get; set; }
    public int BrightnessPct { get; set; }
    public bool SupportsBrightness { get; set; }
    public bool SupportsColor { get; set; }
    public bool SupportsColorTemp { get; set; }
    public int[]? Rgb { get; set; }
    public int ColorTempK { get; set; }
    public string Area { get; set; } = "";
}

public sealed class HaConfigResponse
{
    public string Url { get; set; } = "";
    /// <summary>True when a token is stored (token is never returned).</summary>
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
}

public sealed class HaConfigBody
{
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
}

public sealed class HaConfigSetResponse
{
    public bool Ok { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
}

public sealed class HaEntitiesResponse
{
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public string Error { get; set; } = "";
    public List<HaEntityDto> Entities { get; set; } = new();
}

public sealed class HaSetEntityBody
{
    public string EntityId { get; set; } = "";
    public bool? On { get; set; }
    public int? BrightnessPct { get; set; }
    public int[]? Rgb { get; set; }
    public int? ColorTempK { get; set; }
}

public sealed class HomeAssistantChangedFrame
{
    public long Revision { get; set; }
}
