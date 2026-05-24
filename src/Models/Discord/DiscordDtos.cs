using Nexus.Service.Models;

namespace Nexus.Service.Models.Discord;

public sealed class DiscordConfigResponse : ApiResponse
{
    public string ClientId { get; set; } = "";
    public bool HasClientSecret { get; set; }
    public bool Configured { get; set; }
}

public sealed class DiscordConfigBody
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool? ClearClientSecret { get; set; }
}

public sealed class DiscordStatusResponse : ApiResponse
{
    public bool Ready { get; set; }
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public bool NeedsAuthorization { get; set; }
    public string Reason { get; set; } = "";
    public DiscordUser? User { get; set; }
    public List<DiscordGuild> Guilds { get; set; } = new();
    public DiscordVoiceState? VoiceState { get; set; }
    public List<DiscordNotification> Notifications { get; set; } = new();
}

public sealed class DiscordUser
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
    public string Discriminator { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string? AccentColor { get; set; }
    public string? GlobalName { get; set; }
    public string? BannerColor { get; set; }
}

public sealed class DiscordGuild
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? IconUrl { get; set; }
}

public sealed class DiscordVoiceState
{
    public string ChannelName { get; set; } = "";
    public string GuildName { get; set; } = "";
    public string GuildId { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public bool SelfMute { get; set; }
    public bool SelfDeaf { get; set; }
    public List<DiscordVoiceParticipant> Participants { get; set; } = new();
}

public sealed class DiscordVoiceParticipant
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? GlobalName { get; set; }
    public string? AvatarUrl { get; set; }
    public bool Mute { get; set; }
    public bool Deaf { get; set; }
    public bool Speaking { get; set; }
}

public sealed class DiscordNotification
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string? GuildId { get; set; }
    public long Timestamp { get; set; }
    public string SenderName { get; set; } = "";
    public string SenderAvatarUrl { get; set; } = "";
    public bool MentionEveryone { get; set; }
    public bool MentionUser { get; set; }
}

public sealed class DiscordOpenBody
{
    public string Path { get; set; } = "";
}

public sealed class DiscordVoiceToggleBody
{
    public bool Enabled { get; set; }
}
