using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Lighting;

public sealed class Cs2GsiPayload
{
    [JsonPropertyName("provider")] public Cs2GsiProvider? Provider { get; set; }
    [JsonPropertyName("player")] public Cs2GsiPlayer? Player { get; set; }
    [JsonPropertyName("round")] public Cs2GsiRound? Round { get; set; }
    [JsonPropertyName("bomb")] public Cs2GsiBomb? Bomb { get; set; }
    [JsonPropertyName("map")] public Cs2GsiMap? Map { get; set; }
    [JsonPropertyName("phase_countdowns")] public Cs2GsiPhaseCountdowns? PhaseCountdowns { get; set; }
    [JsonPropertyName("auth")] public Cs2GsiAuth? Auth { get; set; }
}

public sealed class Cs2GsiProvider
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("appid")] public int AppId { get; set; }
    [JsonPropertyName("steamid")] public string? SteamId { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
}

public sealed class Cs2GsiPlayer
{
    [JsonPropertyName("team")] public string? Team { get; set; }
    [JsonPropertyName("activity")] public string? Activity { get; set; }
    [JsonPropertyName("state")] public Cs2GsiPlayerState? State { get; set; }
}

public sealed class Cs2GsiPlayerState
{
    [JsonPropertyName("health")] public int Health { get; set; }
    [JsonPropertyName("armor")] public int Armor { get; set; }
    [JsonPropertyName("helmet")] public bool Helmet { get; set; }
    [JsonPropertyName("defusekit")] public bool Defusekit { get; set; }
    [JsonPropertyName("flashed")] public int Flashed { get; set; }
    [JsonPropertyName("smoked")] public int Smoked { get; set; }
    [JsonPropertyName("burning")] public int Burning { get; set; }
    [JsonPropertyName("money")] public int Money { get; set; }
    [JsonPropertyName("round_kills")] public int RoundKills { get; set; }
}

public sealed class Cs2GsiRound
{
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("bomb")] public string? Bomb { get; set; }
    [JsonPropertyName("win_team")] public string? WinTeam { get; set; }
}

public sealed class Cs2GsiBomb
{
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("countdown")] public string? Countdown { get; set; }
}

public sealed class Cs2GsiMap
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("team_ct")] public Cs2GsiTeamScore? TeamCt { get; set; }
    [JsonPropertyName("team_t")] public Cs2GsiTeamScore? TeamT { get; set; }
}

public sealed class Cs2GsiTeamScore
{
    [JsonPropertyName("score")] public int Score { get; set; }
}

public sealed class Cs2GsiPhaseCountdowns
{
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("phase_ends_in")] public string? PhaseEndsIn { get; set; }
}

public sealed class Cs2GsiAuth
{
    [JsonPropertyName("token")] public string? Token { get; set; }
}
