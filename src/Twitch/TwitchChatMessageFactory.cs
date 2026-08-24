using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Nexus.Service.Models.Twitch;

namespace Nexus.Service.Twitch;

/// <summary>
/// Turns a parsed PRIVMSG line into the wire message the panel renders,
/// splitting the body into text and emote runs.
/// </summary>
public static class TwitchChatMessageFactory
{
    private const int MaxFragments = 60;

    /// <summary>A <c>/me</c> body arrives wrapped as <c>ACTION text</c>.</summary>
    private const char ActionMarker = '\u0001';
    private const string ActionPrefix = "\u0001ACTION ";

    /// <summary>Null when the line is not a renderable chat message.</summary>
    public static TwitchChatMessage? FromPrivmsg(TwitchIrcLine line, long seq)
    {
        if (!string.Equals(line.Command, "PRIVMSG", StringComparison.Ordinal))
        {
            return null;
        }

        if (line.Parameters.Length == 0)
        {
            return null;
        }

        line.Tags.TryGetValue("display-name", out var display);
        var user = string.IsNullOrWhiteSpace(display) ? line.Nick : display;
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        line.Tags.TryGetValue("color", out var color);
        line.Tags.TryGetValue("emotes", out var emotes);

        var fragments = BuildFragments(line.Parameters, emotes);
        if (fragments.Count == 0)
        {
            return null;
        }

        return new TwitchChatMessage
        {
            Seq = seq,
            User = user,
            Color = NormalizeColor(color),
            Fragments = fragments,
        };
    }

    private static string NormalizeColor(string? color)
    {
        if (string.IsNullOrEmpty(color) || color.Length != 7 || color[0] != '#')
        {
            return "";
        }
        for (int i = 1; i < color.Length; i++)
        {
            if (!Uri.IsHexDigit(color[i]))
            {
                return "";
            }
        }
        return color;
    }

    private static List<TwitchChatFragment> BuildFragments(string text, string? emotesTag)
    {
        // Twitch emote ranges index CODE POINTS of the raw body, so an astral
        // char (most emoji) shifts every later range by one when the message is
        // walked as UTF-16.
        var runes = ToRunes(text);

        // The ACTION wrapper is part of the body the ranges are indexed
        // against, so it is dropped by shifting the window rather than by
        // rewriting the string - otherwise every emote in a /me lands short.
        int from = 0;
        int to = runes.Count;
        if (text.StartsWith(ActionPrefix, StringComparison.Ordinal))
        {
            from = ActionPrefix.Length;
            if (to > from && runes[to - 1].Value == ActionMarker)
            {
                to--;
            }
        }

        var ranges = ParseEmoteRanges(emotesTag, runes.Count);
        var fragments = new List<TwitchChatFragment>();

        int cursor = from;
        foreach (var (start, end, id) in ranges)
        {
            if (start < cursor || end >= to)
            {
                continue; // overlapping or outside the visible window
            }
            AppendText(fragments, Slice(runes, cursor, start));
            fragments.Add(new TwitchChatFragment
            {
                Text = Slice(runes, start, end + 1),
                EmoteId = id,
            });
            cursor = end + 1;
            if (fragments.Count >= MaxFragments)
            {
                break;
            }
        }
        AppendText(fragments, Slice(runes, cursor, to));

        if (fragments.Count == 1 && fragments[0].EmoteId.Length == 0 && fragments[0].Text.Trim().Length == 0)
        {
            fragments.Clear();
        }
        return fragments;
    }

    private static void AppendText(List<TwitchChatFragment> fragments, string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        // Merge into a trailing text run so consecutive gaps stay one fragment.
        if (fragments.Count > 0 && fragments[^1].EmoteId.Length == 0)
        {
            fragments[^1].Text += text;
            return;
        }
        fragments.Add(new TwitchChatFragment { Text = text });
    }

    private static List<Rune> ToRunes(string text)
    {
        var runes = new List<Rune>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            runes.Add(rune);
        }
        return runes;
    }

    private static string Slice(List<Rune> runes, int start, int endExclusive)
    {
        if (start >= endExclusive)
        {
            return "";
        }
        var sb = new StringBuilder(endExclusive - start);
        for (int i = start; i < endExclusive; i++)
        {
            sb.Append(runes[i].ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse <c>emotes=25:0-4,12-16/1902:6-10</c> into ordered, in-bounds
    /// ranges. Malformed entries are skipped rather than failing the message.
    /// </summary>
    private static List<(int Start, int End, string Id)> ParseEmoteRanges(string? tag, int runeCount)
    {
        var ranges = new List<(int, int, string)>();
        if (string.IsNullOrEmpty(tag))
        {
            return ranges;
        }

        foreach (var entry in tag.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            if (colon <= 0 || colon == entry.Length - 1)
            {
                continue;
            }
            var id = entry[..colon];
            if (!IsValidEmoteId(id))
            {
                continue;
            }

            foreach (var span in entry[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = span.IndexOf('-');
                if (dash <= 0)
                {
                    continue;
                }
                if (!int.TryParse(span[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int start) ||
                    !int.TryParse(span[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int end))
                {
                    continue;
                }
                if (start < 0 || end < start || end >= runeCount)
                {
                    continue;
                }
                ranges.Add((start, end, id));
            }
        }

        ranges.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return ranges;
    }

    /// <summary>
    /// Emote ids reach the CDN URL verbatim, so the charset is pinned here
    /// (Twitch uses digits and <c>emotesv2_</c>-prefixed tokens). Anything else
    /// is dropped rather than proxied.
    /// </summary>
    public static bool IsValidEmoteId(string id)
    {
        if (id.Length == 0 || id.Length > 64)
        {
            return false;
        }
        foreach (var c in id)
        {
            bool ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || c == '_' || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}
