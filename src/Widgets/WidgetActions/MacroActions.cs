using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Widgets.WidgetActions;

/// <summary>
/// Macro-style host actions: open a URL, simulate a keyboard shortcut,
/// or launch a registered shortcut by id. Mirrors the legacy
/// /panel/macros/* + /shortcuts/launch endpoints — exposed through the
/// widget dispatch channel so a declarative macro button can fire one.
/// </summary>
public static class MacroActions
{
    // Per-URL cooldown for user-visible side effects. A widget granted
    // `macros.openUrl` could otherwise tick every dispatch refresh and
    // spawn browser tabs unattended. Keyed by `openUrl:<absolute-url>` —
    // the same URL is rate-limited globally regardless of which widget
    // dispatched it.
    private const int OpenUrlCooldownMs = 2_000;
    private static readonly ConcurrentDictionary<string, long> _lastDispatchAt = new(StringComparer.Ordinal);

    public static void RegisterAll(WidgetActionRegistry registry)
    {
        registry.Register("macros.openUrl", (services, args, _) =>
        {
            if (args is null || !args.TryGetValue("url", out var urlEl) ||
                urlEl.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult<JsonElement?>(ErrorJson("url is required"));
            }
            var url = urlEl.GetString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return Task.FromResult<JsonElement?>(ErrorJson("invalid url"));
            }
            // Cooldown by URL — refuses repeat dispatches within the
            // window. Pure rate limit; a real user tap two seconds
            // later goes through.
            if (!TryClaimCooldown($"openUrl:{parsed.AbsoluteUri}", OpenUrlCooldownMs))
            {
                return Task.FromResult<JsonElement?>(ErrorJson("rate limited"));
            }
            try
            {
                Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
                return Task.FromResult<JsonElement?>(OkJson("opened"));
            }
            catch (Exception ex)
            {
                return Task.FromResult<JsonElement?>(ErrorJson(ex.Message));
            }
        });

        registry.Register("macros.shortcut", (services, args, _) =>
        {
            // Keyboard simulation isn't implemented yet (matches the legacy
            // /panel/macros/shortcut stub). We accept + acknowledge so the
            // dispatch contract is stable when it lands.
            if (args is null || !args.TryGetValue("keys", out var keysEl) ||
                keysEl.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult<JsonElement?>(ErrorJson("keys is required"));
            }
            return Task.FromResult<JsonElement?>(OkJson("shortcut received (simulation pending)"));
        });

        registry.Register("macros.launchApp", (services, args, _) =>
        {
            if (args is null || !args.TryGetValue("appId", out var idEl) ||
                idEl.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult<JsonElement?>(ErrorJson("appId is required"));
            }
            var appId = idEl.GetString() ?? "";
            if (string.IsNullOrEmpty(appId))
            {
                return Task.FromResult<JsonElement?>(ErrorJson("appId is required"));
            }
            try
            {
                var provider = services.GetRequiredService<IShortcutsProvider>();
                provider.Launch(appId);
                return Task.FromResult<JsonElement?>(OkJson("launched"));
            }
            catch (Exception ex)
            {
                return Task.FromResult<JsonElement?>(ErrorJson(ex.Message));
            }
        });
    }

    /// <summary>
    /// Claims a cooldown slot for the given key, returning true if the
    /// caller is allowed to proceed. Concurrent claims on the same key
    /// within the cooldown window return false.
    /// </summary>
    private static bool TryClaimCooldown(string key, int cooldownMs)
    {
        var now = Environment.TickCount64;
        var existing = _lastDispatchAt.GetOrAdd(key, 0);
        if (now - existing < cooldownMs) return false;
        _lastDispatchAt[key] = now;
        return true;
    }

    // AOT-safe ack helpers. Anonymous-type serialisation fails under the
    // trimmer; the typed DTO + AppJsonContext use the source-gen contract.
    private static JsonElement OkJson(string message)
    {
        var dto = new WidgetActionAckDto { Ok = true, Message = message };
        var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.WidgetActionAckDto);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement ErrorJson(string message)
    {
        var dto = new WidgetActionAckDto { Ok = false, Error = message };
        var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.WidgetActionAckDto);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static IReadOnlyList<string> AllActions => new[]
    {
        "macros.openUrl", "macros.shortcut", "macros.launchApp",
    };
}
