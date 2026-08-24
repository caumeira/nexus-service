using System.Diagnostics;
using Nexus.Service.Models;
using Nexus.Service.Models.Discord;
using Nexus.Service.Persistence;
using Nexus.Service.Security;

namespace Nexus.Service.Discord;

public sealed class DiscordProvider : IDiscordProvider
{
    private readonly IConfigStore _store;
    private readonly DiscordRichPresenceService _presence;

    public DiscordProvider(IConfigStore store, DiscordRichPresenceService presence)
    {
        _store = store;
        _presence = presence;
    }

    public DiscordStatusResponse GetStatus()
    {
        var config = ReadConfig();
        var reason = config.Configured
            ? "Discord OAuth token exchange is configured, but RPC authorization is not connected yet"
            : "Discord OAuth is not configured";

        return new DiscordStatusResponse
        {
            Ready = false,
            Connected = false,
            Configured = config.Configured,
            NeedsAuthorization = config.Configured,
            Reason = reason,
            Error = true,
            Msg = reason,
        };
    }

    public ApiResponse Launch()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    ArgumentList = { "-a", "Discord" },
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("discord://-/")
                {
                    UseShellExecute = true,
                });
            }

            return ApiResponse.Ok("launched");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to launch Discord: {ex.Message}", ex);
        }
    }

    public ApiResponse Open(DiscordOpenBody body)
    {
        var path = body.Path.Trim().TrimStart('/');
        if (path.Length == 0)
        {
            return ApiResponse.Fail("Discord path is required");
        }

        var uri = path.StartsWith("channels/", StringComparison.OrdinalIgnoreCase)
            ? $"discord://-/{path}"
            : $"discord://-/channels/{path}";

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    ArgumentList = { uri },
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(uri)
                {
                    UseShellExecute = true,
                });
            }

            return ApiResponse.Ok("opened");
        }
        catch (Exception ex)
        {
            return ApiResponse.Fail($"failed to open Discord: {ex.Message}", ex);
        }
    }

    public ApiResponse SetMute(bool muted) =>
        ApiResponse.Fail("Discord voice controls require RPC authorization");

    public ApiResponse SetDeaf(bool deafened) =>
        ApiResponse.Fail("Discord voice controls require RPC authorization");

    public ApiResponse DisconnectVoice() =>
        ApiResponse.Fail("Discord voice controls require RPC authorization");

    public DiscordPresenceResponse GetPresence() => BuildPresence();

    public DiscordPresenceResponse SetPresence(DiscordPresenceBody body)
    {
        _store.Update(s =>
        {
            if (body.Enabled is not null)
            {
                s.Discord.RichPresenceEnabled = body.Enabled.Value;
            }
            if (body.Preset is not null)
            {
                s.Discord.RichPresencePreset = DiscordRichPresence.Normalize(body.Preset);
            }
        });

        // The presence loop wakes on the store's OnChanged, so the connection
        // is reconciled asynchronously - Connected here is the pre-apply value.
        return BuildPresence();
    }

    private DiscordPresenceResponse BuildPresence()
    {
        var settings = _store.Load().Discord;
        var available = DiscordRichPresenceService.IsAvailable;
        return new DiscordPresenceResponse
        {
            Available = available,
            Enabled = settings.RichPresenceEnabled,
            Preset = DiscordRichPresence.Normalize(settings.RichPresencePreset),
            Presets = new List<string>(DiscordRichPresence.Presets),
            Connected = _presence.IsConnected,
            Msg = available ? "Ok" : "Discord Rich Presence is not available in this build",
        };
    }

    private DiscordRuntimeConfig ReadConfig()
    {
        var settings = _store.Load().Discord;
        var clientId = FirstNonEmpty(settings.ClientId, Environment.GetEnvironmentVariable("NEXUS_DISCORD_CLIENT_ID"));
        var clientSecret = FirstNonEmpty(
            SecretProtector.Unprotect(settings.ClientSecret),
            Environment.GetEnvironmentVariable("NEXUS_DISCORD_CLIENT_SECRET"));
        return new DiscordRuntimeConfig(clientId, clientSecret.Length > 0);
    }

    private static string FirstNonEmpty(string? first, string? second)
    {
        var trimmed = first?.Trim() ?? "";
        if (trimmed.Length > 0)
        {
            return trimmed;
        }
        return second?.Trim() ?? "";
    }

    private sealed record DiscordRuntimeConfig(string ClientId, bool HasClientSecret)
    {
        public bool Configured => ClientId.Length > 0 && HasClientSecret;
    }
}
