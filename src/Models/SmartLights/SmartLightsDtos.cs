using System.Collections.Generic;

namespace Nexus.Service.Models.SmartLights;

/// <summary>One paired smart light as the Smart Lights page sees it.</summary>
public class SmartLightDto
{
    public string Id { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public bool Online { get; set; }
    public bool Enabled { get; set; }
    public int LedCount { get; set; }
}

public class GetSmartLightsResponse
{
    public List<SmartLightDto> Devices { get; set; } = new();
}

public class DiscoverSmartLightsBody
{
    public string Brand { get; set; } = "";
}

public class DiscoveredSmartLightDto
{
    public string Brand { get; set; } = "";
    public string Host { get; set; } = "";
    public string Name { get; set; } = "";
    public string StableKey { get; set; } = "";
    public bool AlreadyPaired { get; set; }
}

public class DiscoverSmartLightsResponse
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public List<DiscoveredSmartLightDto> Devices { get; set; } = new();
}

public class PairSmartLightBody
{
    public string Brand { get; set; } = "";
    public string Host { get; set; } = "";
    public string StableKey { get; set; } = "";
    public string Name { get; set; } = "";
}

public class PairSmartLightResponse
{
    public bool Ok { get; set; }
    /// <summary>Machine-readable hint, e.g. "link-button" when the user must press
    /// the Hue bridge button and retry.</summary>
    public string Error { get; set; } = "";
    public int Added { get; set; }
    public string Message { get; set; } = "";
}

public class RemoveSmartLightBody
{
    public string Id { get; set; } = "";
}
