#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Helper;

/// <summary>
/// Helper-side dispatch table. Each domain registers a handler keyed on the
/// envelope <c>Type</c> string; the read loop calls
/// <see cref="DispatchAsync"/> for every inbound envelope.
///
/// This replaces the previous god-switch in <c>HelperClientCommands</c>:
/// each domain now owns its handler under <c>Helper/Domains/</c> and calls
/// <see cref="Register"/> at helper startup. Adding a domain no longer
/// edits this file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperHandlerRegistry
{
    public delegate Task<HelperResult> Handler(HelperEnvelope env, CancellationToken ct);

    private readonly Dictionary<string, Handler> _handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Bind a handler for one envelope type. Last registration wins, which
    /// keeps the helper bootstrap (one-shot at startup) trivial.
    /// </summary>
    public void Register(string type, Handler handler) => _handlers[type] = handler;

    /// <summary>
    /// Find and invoke the handler for an envelope, or return an
    /// <c>"unknown type"</c> failure so the caller's RPC awaiter unblocks.
    /// Exceptions are caught and turned into <see cref="HelperResult.Fail"/>
    /// so one buggy domain cannot kill the read loop.
    /// </summary>
    public async Task<HelperResult> DispatchAsync(HelperEnvelope env, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(env.Type, out var handler))
            return HelperResult.Fail(env.Id, $"unknown type {env.Type}");
        try { return await handler(env, ct).ConfigureAwait(false); }
        catch (Exception ex) { return HelperResult.Fail(env.Id, ex.Message); }
    }
}

/// <summary>
/// Helpers for domain handlers to build common result shapes without
/// repeating envelope-id plumbing. Domain handlers usually return
/// <c>env.Ok()</c> or <c>HelperResult.Fail(env.Id, "...")</c>.
/// </summary>
public static class HelperResultExtensions
{
    public static HelperResult Ok(this HelperEnvelope env) => HelperResult.OkFor(env.Id);
}
#endif
