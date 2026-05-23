using Qos.Service.Models;

namespace Qos.Service.Models.Steam;

public enum SteamPersonaState
{
    Offline = 0,
    Online = 1,
    Busy = 2,
    Away = 3,
    Snooze = 4,
    LookingToTrade = 5,
    LookingToPlay = 6,
}

public sealed class SteamConfigResponse : ApiResponse
{
    public bool HasApiKey { get; set; }
    public string SteamId { get; set; } = "";
    public string AutoDetectedSteamId { get; set; } = "";
}

public sealed class SteamConfigBody
{
    public string? ApiKey { get; set; }
    public string? SteamId { get; set; }
    public bool? ClearApiKey { get; set; }
}

public sealed class SteamStatusResponse : ApiResponse
{
    public bool Ready { get; set; }
    public bool HasApiKey { get; set; }
    public string SteamId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class SteamProfileResponse : ApiResponse
{
    public SteamPlayerSummary? Profile { get; set; }
    public int? Level { get; set; }
}

public sealed class SteamPlayerSummary
{
    public string SteamId { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public string ProfileUrl { get; set; } = "";
    public string Avatar { get; set; } = "";
    public string AvatarMedium { get; set; } = "";
    public string AvatarFull { get; set; } = "";
    public SteamPersonaState PersonaState { get; set; }
    public int CommunityVisibilityState { get; set; }
    public long? LastLogoff { get; set; }
    public string? GameExtraInfo { get; set; }
    public string? GameId { get; set; }
}

public sealed class SteamRecentGame
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public int Playtime2Weeks { get; set; }
    public int PlaytimeForever { get; set; }
    public string IconHash { get; set; } = "";
}

public sealed class SteamOwnedGame
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public int PlaytimeForever { get; set; }
    public int? Playtime2Weeks { get; set; }
    public string IconHash { get; set; } = "";
}

public sealed class SteamFriendSummary
{
    public string SteamId { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public string AvatarMedium { get; set; } = "";
    public SteamPersonaState PersonaState { get; set; }
    public string? GameExtraInfo { get; set; }
    public long FriendSince { get; set; }
}

public sealed class SteamAchievement
{
    public string ApiName { get; set; } = "";
    public int Achieved { get; set; }
    public long UnlockTime { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public sealed class SteamCurrentPlayers
{
    public int PlayerCount { get; set; }
}

public sealed class SteamNewsItem
{
    public string Gid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Author { get; set; } = "";
    public string Contents { get; set; } = "";
    public string FeedLabel { get; set; } = "";
    public long Date { get; set; }
    public string FeedName { get; set; } = "";
    public int FeedType { get; set; }
    public int AppId { get; set; }
}

public sealed class SteamGlobalAchievement
{
    public string Name { get; set; } = "";
    public double Percent { get; set; }
}

public sealed class SteamUserStat
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
}

public sealed class SteamAppDetails
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public string HeaderImage { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public List<string> Developers { get; set; } = new();
    public List<string> Publishers { get; set; } = new();
    public List<string> Genres { get; set; } = new();
    public string ReleaseDate { get; set; } = "";
    public bool IsFree { get; set; }
    public int? MetacriticScore { get; set; }
}
