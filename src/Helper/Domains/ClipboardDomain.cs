#if WINDOWS
using System;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>clipboard.setText</c>. Service-to-helper. Clipboards are
/// per-session: a set from the Session-0 service lands on an invisible
/// clipboard, so the user-session helper applies it instead.
/// </summary>
public sealed class ClipboardSetTextPayload
{
    public string Text { get; set; } = "";
}

// JSON source-gen registration lives in src/Serialization/AppJsonContext.cs
// (see note in LifecycleDomain.cs).

[SupportedOSPlatform("windows")]
public static class ClipboardCommands
{
    /// <summary>True when a user-session helper applied the text.</summary>
    public static async Task<bool> SetTextAsync(HelperRegistry registry, string text, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null)
        {
            return false;
        }
        var result = await conn.SendCommandAsync(
            "clipboard.setText",
            new ClipboardSetTextPayload { Text = text ?? "" },
            AppJsonContext.Default.ClipboardSetTextPayload,
            timeoutMs: 5000,
            ct: ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            // Distinguishes pipe skew ("unknown type") from timeout from a
            // helper-side Set-Clipboard failure.
            Nexus.Service.Platform.ServiceLog.Warn($"[clipboard-win] helper set failed: {result.Error}");
        }
        return result.Ok;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ClipboardHandler
{
    private readonly Func<string, bool> _setText;

    public ClipboardHandler(Func<string, bool> setText) => _setText = setText;

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("clipboard.setText", (env, _) =>
        {
            var ok = false;
            if (env.Payload is not null)
            {
                var p = JsonSerializer.Deserialize(env.Payload.Value, AppJsonContext.Default.ClipboardSetTextPayload);
                ok = p is not null && _setText(p.Text ?? "");
            }
            return Task.FromResult(ok ? env.Ok() : HelperResult.Fail(env.Id, "clipboard set failed"));
        });
    }
}
#endif
