using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Actions;

namespace Nexus.Service.Widgets.AppActions;

/// <summary>
/// Host action letting an SDK app hand a link to the user's browser or mail
/// client. Sandboxed workers have no other way out to a site, so the gate is
/// narrow: an https link only to a host the app's own manifest already
/// allowlists for fetching, or a plain mailto address.
/// </summary>
public static partial class OpenUrlActions
{
    public const int MaxUrlLength = 2048;

    // Unreserved, gen-delims, sub-delims and the percent sign of RFC 3986.
    private const string UrlPunctuation = "-._~:/?#[]@!$&'()*+,;=%";

    public static void RegisterAll(AppActionRegistry registry)
    {
        registry.Register("system.openUrl", async (services, args, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var appId = AppActionHelpers.Str(args, "__appId") ?? "";
            var url = AppActionHelpers.Str(args, "url") ?? "";

            // The allowlist comes from the installed manifest, never from the
            // caller's args, so a tampered worker cannot widen its own reach.
            var apps = services.GetRequiredService<AppRegistry>();
            IReadOnlyList<string> allowlist = apps.TryGet(appId, out var entry)
                ? entry.Manifest.Capabilities.NetFetch
                : Array.Empty<string>();

            if (!TryValidate(url, allowlist, out var target, out _))
            {
                return AppActionHelpers.Ack(false, "url not permitted");
            }

            var opened = await services.GetRequiredService<SystemActions>()
                .LaunchUrlAsync(target).ConfigureAwait(false);
            return opened.Error
                ? AppActionHelpers.Ack(false, "failed to open url")
                : AppActionHelpers.Ack(true);
        });
    }

    public static IReadOnlyList<string> AllActions => new[] { "system.openUrl" };

    /// <summary>
    /// Decides whether <paramref name="url"/> may be handed to the platform
    /// shell on behalf of an app whose manifest allowlists
    /// <paramref name="allowlist"/>, and yields the exact string to open.
    /// </summary>
    internal static bool TryValidate(string? url, IReadOnlyList<string>? allowlist, out string target, out string reason)
    {
        target = "";
        var trimmed = url?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            reason = "url is required";
            return false;
        }
        if (trimmed.Length > MaxUrlLength)
        {
            reason = "url is too long";
            return false;
        }
        foreach (var ch in trimmed)
        {
            // Only what a URL may carry unencoded. That drops whitespace,
            // control and non-ASCII characters, and the quoting characters a
            // protocol-handler command template would read as structure.
            if (!char.IsAsciiLetterOrDigit(ch) && !UrlPunctuation.Contains(ch))
            {
                reason = "url has an unsupported character";
                return false;
            }
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            reason = "url must be absolute";
            return false;
        }

        if (string.Equals(parsed.Scheme, "mailto", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsPlainMailto(trimmed[(trimmed.IndexOf(':') + 1)..]))
            {
                reason = "mailto must be one address with subject or body only";
                return false;
            }
            target = trimmed;
            reason = "";
            return true;
        }

        if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            reason = "scheme is not permitted";
            return false;
        }
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            reason = "url carries userinfo";
            return false;
        }
        if (!AppProxyService.HostInAllowlist(parsed.Host, allowlist ?? Array.Empty<string>()))
        {
            reason = $"host '{parsed.Host}' is not in the manifest's net.fetch allowlist";
            return false;
        }

        target = parsed.AbsoluteUri;
        reason = "";
        return true;
    }

    private static bool IsPlainMailto(string rest)
    {
        // A mailto carries no fragment, so a hash is only ever an attempt to
        // hide something past the address.
        if (rest.Length == 0 || rest.Contains('#')) return false;
        var mark = rest.IndexOf('?');
        var address = mark < 0 ? rest : rest[..mark];
        if (!MailAddress().IsMatch(address)) return false;
        if (mark < 0) return true;

        foreach (var field in rest[(mark + 1)..].Split('&'))
        {
            if (field.Length == 0) return false;
            var eq = field.IndexOf('=');
            var key = eq < 0 ? field : field[..eq];
            if (!string.Equals(key, "subject", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(key, "body", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    // Anchored, so a second address (which needs a second @) cannot ride along.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MailAddress();
}
