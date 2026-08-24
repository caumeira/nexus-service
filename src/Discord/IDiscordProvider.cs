using Nexus.Service.Models;
using Nexus.Service.Models.Discord;

namespace Nexus.Service.Discord;

public interface IDiscordProvider
{
    DiscordStatusResponse GetStatus();
    ApiResponse Launch();
    ApiResponse Open(DiscordOpenBody body);
    ApiResponse SetMute(bool muted);
    ApiResponse SetDeaf(bool deafened);
    ApiResponse DisconnectVoice();
    DiscordPresenceResponse GetPresence();
    DiscordPresenceResponse SetPresence(DiscordPresenceBody body);
}
