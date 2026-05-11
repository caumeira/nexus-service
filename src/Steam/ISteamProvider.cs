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
    ApiResponse Launch();
}
