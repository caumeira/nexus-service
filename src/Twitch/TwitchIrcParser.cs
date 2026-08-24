using System;
using System.Collections.Generic;

namespace Nexus.Service.Twitch;

/// <summary>
/// One parsed IRC line off the Twitch chat socket. <see cref="Tags"/> is the
/// IRCv3 tag map (empty when the line carried none); <see cref="Nick"/> is the
/// sender's login, <see cref="Parameters"/> the trailing text.
/// </summary>
public sealed record TwitchIrcLine(
    string Command,
    string Channel,
    string Nick,
    string Parameters,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Parser for the Twitch IRCv3 line format
/// (<c>@tags :nick!user@host COMMAND #channel :message</c>). Only the shape
/// Twitch actually emits is handled - this is not a general IRC parser.
/// </summary>
public static class TwitchIrcParser
{
    private static readonly IReadOnlyDictionary<string, string> NoTags =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    /// <summary>Parse one line; null when it carries no command we model.</summary>
    public static TwitchIrcLine? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var span = line.AsSpan().TrimEnd("\r\n");
        int idx = 0;

        IReadOnlyDictionary<string, string> tags = NoTags;
        if (idx < span.Length && span[idx] == '@')
        {
            int end = span[idx..].IndexOf(' ');
            if (end < 0)
            {
                return null;
            }
            tags = ParseTags(span.Slice(idx + 1, end - 1));
            idx += end + 1;
        }

        string nick = "";
        if (idx < span.Length && span[idx] == ':')
        {
            int end = span[idx..].IndexOf(' ');
            if (end < 0)
            {
                return null;
            }
            var source = span.Slice(idx + 1, end - 1);
            int bang = source.IndexOf('!');
            nick = (bang > 0 ? source[..bang] : source).ToString();
            idx += end + 1;
        }

        // The command section runs to the trailing-parameter marker (" :"), or
        // to end of line when there is none. A bare ':' inside the command
        // section cannot occur, so searching for the space-colon pair is safe
        // where searching for ':' alone is not (a message body may contain one).
        var rest = span[idx..];
        int trailing = rest.IndexOf(" :", StringComparison.Ordinal);
        var commandPart = (trailing >= 0 ? rest[..trailing] : rest).Trim();
        string parameters = trailing >= 0 ? rest[(trailing + 2)..].ToString() : "";

        if (commandPart.Length == 0)
        {
            return null;
        }

        int sp = commandPart.IndexOf(' ');
        string command = (sp > 0 ? commandPart[..sp] : commandPart).ToString();
        string channel = "";
        if (sp > 0)
        {
            var args = commandPart[(sp + 1)..].Trim();
            int argEnd = args.IndexOf(' ');
            var first = argEnd > 0 ? args[..argEnd] : args;
            if (first.Length > 0 && first[0] == '#')
            {
                channel = first[1..].ToString();
            }
        }

        return new TwitchIrcLine(command, channel, nick, parameters, tags);
    }

    /// <summary>
    /// Split the IRCv3 tag blob. Values are unescaped per the spec
    /// (<c>\s</c> space, <c>\:</c> semicolon, <c>\\</c> backslash, <c>\r</c>,
    /// <c>\n</c>); a valueless tag maps to the empty string.
    /// </summary>
    private static Dictionary<string, string> ParseTags(ReadOnlySpan<char> raw)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        while (!raw.IsEmpty)
        {
            int semi = raw.IndexOf(';');
            var pair = semi >= 0 ? raw[..semi] : raw;
            raw = semi >= 0 ? raw[(semi + 1)..] : default;

            int eq = pair.IndexOf('=');
            if (eq < 0)
            {
                if (pair.Length > 0)
                {
                    tags[pair.ToString()] = "";
                }
                continue;
            }

            var key = pair[..eq];
            if (key.Length == 0)
            {
                continue;
            }
            tags[key.ToString()] = Unescape(pair[(eq + 1)..]);
        }
        return tags;
    }

    private static string Unescape(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value.ToString();
        }

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }
            i++;
            sb.Append(value[i] switch
            {
                's' => ' ',
                ':' => ';',
                'r' => '\r',
                'n' => '\n',
                '\\' => '\\',
                _ => value[i],
            });
        }
        return sb.ToString();
    }
}
