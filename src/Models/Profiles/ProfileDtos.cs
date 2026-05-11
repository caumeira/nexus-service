using System.Collections.Generic;
using Qos.Service.Persistence;

namespace Qos.Service.Models.Profiles;

public class ListProfilesResponse : ApiResponse
{
    public List<ProfileEntry> Profiles { get; set; } = new();
    public string ActiveId { get; set; } = "";
}

public class ProfileResponse : ApiResponse
{
    public ProfileEntry? Profile { get; set; }
}

public class SwitchProfileResponse : ApiResponse
{
    public string Switched { get; set; } = "";
    public UiSettings? Ui { get; set; }
}

public class CreateProfileBody
{
    public string Name { get; set; } = "";
}

public class RenameProfileBody
{
    public string Name { get; set; } = "";
}

public class ImportProfileBody
{
    public string Name { get; set; } = "";
    public QosSettings? Data { get; set; }
}

public sealed class ProfileExport
{
    public string? Name { get; set; }
    public QosSettings? Settings { get; set; }
}

public class SharingResponse : ApiResponse
{
    public string? PrimaryProfileId { get; set; }
    public List<string> SharedCategories { get; set; } = new();
    public List<string> AllCategories { get; set; } = new();
}

public class SetPrimaryBody
{
    public string ProfileId { get; set; } = "";
}

public class SetCategorySharedBody
{
    public string Category { get; set; } = "";
    public bool Shared { get; set; }
}
