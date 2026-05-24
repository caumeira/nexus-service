using System.Collections.Generic;

namespace Nexus.Service.Persistence;

public sealed class ProfileManifest
{
    public string ActiveProfileId { get; set; } = "";
    public List<ProfileEntry> Profiles { get; set; } = new();
}

public sealed class ProfileEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
