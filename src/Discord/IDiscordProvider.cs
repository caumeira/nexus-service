using Qos.Service.Models;
using Qos.Service.Models.Discord;

namespace Qos.Service.Discord;

public interface IDiscordProvider
{
    DiscordConfigResponse GetConfig();
    void SetConfig(DiscordConfigBody body);
    DiscordStatusResponse GetStatus();
    ApiResponse Launch();
    ApiResponse Open(DiscordOpenBody body);
    ApiResponse SetMute(bool muted);
    ApiResponse SetDeaf(bool deafened);
    ApiResponse DisconnectVoice();
}
