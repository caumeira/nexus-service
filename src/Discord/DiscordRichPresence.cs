using System.Text.Json;

namespace Nexus.Service.Discord;

/// <summary>
/// The Rich Presence status lines a user can pick from. Discord renders the
/// application's own name as the first line ("Playing Nexus"), so these are the
/// detail line under it. They are brand voice rather than UI chrome and are
/// deliberately not localized, matching how the rest of the presence payload
/// (application name, asset tooltips) is fixed by Discord.
/// </summary>
public static class DiscordRichPresence
{
    public static readonly IReadOnlyList<string> Presets = new[]
    {
        "Tuning my rig",
        "Watching temps",
        "Dialing in fan curves",
        "Painting in RGB",
        "Chasing frames",
        "Stress testing",
        "Cable managing",
        "Just vibin'",
    };

    public static string Default => Presets[0];

    /// <summary>
    /// Maps a stored or client-supplied preset onto the known set. Anything
    /// unrecognized (a hand-edited settings file, a preset retired in a later
    /// build) falls back rather than being published verbatim.
    /// </summary>
    public static string Normalize(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
        {
            return Default;
        }

        var trimmed = preset.Trim();
        foreach (var candidate in Presets)
        {
            if (string.Equals(candidate, trimmed, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return Default;
    }

    /// <summary>
    /// Writes the activity object Discord renders under "Playing Nexus": the
    /// chosen preset as the detail line, an elapsed timer from
    /// <paramref name="startUnixSeconds"/>, the application's own art, and a
    /// single link button other users see on the profile card.
    /// </summary>
    public static void WriteActivity(Utf8JsonWriter writer, string preset, long startUnixSeconds)
    {
        writer.WriteString("details", Normalize(preset));
        writer.WriteStartObject("timestamps");
        writer.WriteNumber("start", startUnixSeconds);
        writer.WriteEndObject();
        writer.WriteStartObject("assets");
        writer.WriteString("large_image", LargeImageKey);
        writer.WriteString("large_text", "Nexus");
        writer.WriteEndObject();
        writer.WriteStartArray("buttons");
        writer.WriteStartObject();
        writer.WriteString("label", "Get Nexus");
        writer.WriteString("url", "https://hellonexus.com");
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    /// <summary>Asset key uploaded under Rich Presence art on the Discord application.</summary>
    internal const string LargeImageKey = "nexus";
}
