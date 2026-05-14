using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Qos.Service.Widgets;

/// <summary>
/// Host-action registry. Tier 1 / Tier 2 widgets call into the host's
/// internal services by POSTing to <c>/widgets-api/dispatch</c> with
/// <c>{ widgetId, action, args }</c>; this registry maps action names
/// to handlers that pull from the host's service collection.
///
/// Two reasons this is a registry rather than just routes:
///  1. Per-widget capability allowlists (the manifest declares which
///     actions it wants — host validates before invoking).
///  2. Decouples host endpoint paths from the SDK contract — when we
///     rename <c>/displays</c> later, widgets aren't broken.
/// </summary>
public sealed class WidgetActionRegistry
{
    private readonly Dictionary<string, ActionHandler> _handlers = new(StringComparer.Ordinal);

    public delegate Task<JsonElement?> ActionHandler(
        IServiceProvider services,
        Dictionary<string, JsonElement>? args,
        CancellationToken ct);

    public void Register(string action, ActionHandler handler)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        _handlers[action] = handler;
    }

    public bool TryGet(string action, out ActionHandler handler)
    {
        return _handlers.TryGetValue(action, out handler!);
    }

    public IReadOnlyCollection<string> RegisteredActions => _handlers.Keys;
}
