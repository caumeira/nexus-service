using Nexus.Service.Auth;
using Nexus.Service.Discord;
using Nexus.Service.Models.Discord;

namespace Nexus.Service.Routes;

public static class DiscordRoutes
{
    public static void MapDiscordEndpoints(this WebApplication app)
    {
        app.MapGet("/api/discord/config", (IDiscordProvider discord) => discord.GetConfig()).AllowPanel();
        app.MapPost("/api/discord/config", (DiscordConfigBody body, IDiscordProvider discord) =>
        {
            discord.SetConfig(body);
            return discord.GetConfig();
        }).AllowPanel();
        app.MapGet("/api/discord/status", (IDiscordProvider discord) => discord.GetStatus()).AllowPanel();
        app.MapPost("/api/discord/launch", (IDiscordProvider discord) => discord.Launch()).AllowPanel();
        app.MapPost("/api/discord/open", (DiscordOpenBody body, IDiscordProvider discord) => discord.Open(body)).AllowPanel();
        app.MapPost("/api/discord/voice/mute", (DiscordVoiceToggleBody body, IDiscordProvider discord) =>
            discord.SetMute(body.Enabled)).AllowPanel();
        app.MapPost("/api/discord/voice/deaf", (DiscordVoiceToggleBody body, IDiscordProvider discord) =>
            discord.SetDeaf(body.Enabled)).AllowPanel();
        app.MapPost("/api/discord/voice/disconnect", (IDiscordProvider discord) => discord.DisconnectVoice()).AllowPanel();
    }
}
