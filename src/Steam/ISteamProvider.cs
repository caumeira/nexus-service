using Qos.Service.Models;
using Qos.Service.Models.Steam;

namespace Qos.Service.Steam;

public interface ISteamProvider
{
    SteamConfigResponse GetConfig();
    void SetConfig(SteamConfigBody body);
    SteamStatusResponse GetStatus();
    Task<SteamProfileResponse> GetProfileAsync(CancellationToken cancellationToken);
    Task<List<SteamRecentGame>> GetRecentGamesAsync(CancellationToken cancellationToken);
    Task<List<SteamOwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken);
    Task<List<SteamFriendSummary>> GetFriendsAsync(CancellationToken cancellationToken);
    Task<List<SteamAchievement>> GetAchievementsAsync(int appId, CancellationToken cancellationToken);
    Task<SteamCurrentPlayers> GetCurrentPlayersAsync(int appId, CancellationToken cancellationToken);
    Task<List<SteamNewsItem>> GetNewsAsync(int appId, int count, int maxLength, CancellationToken cancellationToken);
    Task<List<SteamGlobalAchievement>> GetGlobalAchievementsAsync(int appId, CancellationToken cancellationToken);
    Task<List<SteamUserStat>> GetUserStatsAsync(int appId, CancellationToken cancellationToken);
    Task<SteamAppDetails?> GetAppDetailsAsync(int appId, CancellationToken cancellationToken);
    ApiResponse Launch(int? appId = null);
}
