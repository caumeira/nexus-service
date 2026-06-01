using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Relay;

/// <summary>
/// In-process REST-over-relay dispatcher. The off-LAN panel's ordinary fetch()
/// calls (device list, layout, controls) can't reach the PC's local HTTP, so
/// they are tunneled over the relay's <c>rid_http</c> channel as sealed JSON
/// request frames. <see cref="RelayHttpTunnelLink"/> decrypts each one and hands
/// it here; we run it straight through the service's OWN endpoint pipeline —
/// the exact same routing, CORS, security-header, and route handlers the LAN
/// path uses — and capture the status + body to seal back.
///
/// Authorization model. The relay session is ALREADY authenticated end-to-end
/// (only a holder of the session token can derive <c>rid_http</c> and the AEAD
/// key, and the relay forwards opaque ciphertext). So the tunneled request is
/// dispatched authorized as that session's phone-session id WITHOUT the phone
/// re-presenting its bearer/cookie. Two pieces of per-request server-side state
/// carry that decision into the pipeline:
///   • <c>HttpContext.Items["PhoneSessionId"]</c> — the session the request acts as.
///   • <c>HttpContext.Items[<see cref="TrustedRelayDispatchKey"/>]</c> set to the
///     identity sentinel <see cref="TrustedMarker"/> — proof the request entered
///     in-process from THIS dispatcher.
/// <see cref="PathAuthMiddleware"/> honors the marker (skips token validation and
/// the CSRF / panel-allow checks, still enforcing the remote-control killswitch).
/// Because <c>HttpContext.Items</c> is created fresh per request by the server
/// and is never populated from request headers/body/query, a network caller can
/// NOT inject either key; only this dispatcher sets them. The marker is compared
/// by reference identity against a private sentinel, so even guessing the key is
/// insufficient — the value object is unreachable outside this assembly.
///
/// AOT-safe: no reflection, source-generated JSON only, in-box types only.
/// </summary>
public sealed class RelayHttpDispatcher
{
    /// <summary>
    /// <c>HttpContext.Items</c> key flagging an in-process trusted relay dispatch.
    /// The value MUST be <see cref="TrustedMarker"/> (reference identity); the key
    /// alone is not sufficient.
    /// </summary>
    public const string TrustedRelayDispatchKey = "Nexus.TrustedRelayDispatch";

    /// <summary>
    /// Path the synthetic priming request targets — matches no route, so it
    /// 404s harmlessly while letting the capture middleware record the pipeline.
    /// </summary>
    public const string PrimePath = "/__nexus_relay_http_prime__";

    /// <summary>Hard cap on a tunneled request OR response body (~1 MB).</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private const string ContentTypeJson = "application/json";

    /// <summary>
    /// Private identity sentinel. <see cref="PathAuthMiddleware"/> compares the
    /// items value against this exact reference; nothing outside this assembly
    /// can obtain it, so the trusted-dispatch decision cannot be forged.
    /// </summary>
    internal static readonly object TrustedMarker = new();

    private readonly IServiceProvider _services;
    private volatile RequestDelegate? _pipeline;

    public RelayHttpDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>True once the endpoint pipeline has been captured + primed.</summary>
    public bool IsReady => _pipeline is not null;

    /// <summary>
    /// Records the captured downstream pipeline (the full middleware + endpoint
    /// chain after the capture middleware). Set once by that middleware on the
    /// priming pass; idempotent.
    /// </summary>
    internal void SetPipeline(RequestDelegate pipeline)
    {
        // First writer wins; the chain is identical on every request.
        Interlocked.CompareExchange(ref _pipeline, pipeline, null);
    }

    /// <summary>
    /// Dispatch one tunneled request through the service's own pipeline,
    /// authorized as <paramref name="phoneSessionId"/>. Enforces the path
    /// allowlist and body caps first (off-allowlist ⇒ 403, oversized ⇒ 413),
    /// then runs the real handlers and captures the response. Never throws for
    /// an application-level failure — a handler fault surfaces as a 500 in the
    /// returned response so the tunnel can always reply.
    /// </summary>
    public async Task<RelayHttpResponse> DispatchAsync(
        RelayHttpRequest request, string phoneSessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.Path ?? string.Empty;
        if (!RelayHttpAllowlist.IsAllowed(request.Method, path))
            return Reject(request.Id, StatusCodes.Status403Forbidden, "path not permitted over relay");

        if (request.Body is { Length: > MaxBodyBytes })
            return Reject(request.Id, StatusCodes.Status413PayloadTooLarge, "request body exceeds relay cap");

        var pipeline = _pipeline;
        if (pipeline is null)
            return Reject(request.Id, StatusCodes.Status503ServiceUnavailable, "relay http dispatch not ready");

        // Each tunneled request gets its own DI scope, exactly like a real
        // request, so scoped services resolve correctly and are disposed after.
        await using var scope = _services.CreateAsyncScope();
        var ctx = BuildContext(request, phoneSessionId, scope.ServiceProvider, ct);

        var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;

        try
        {
            await pipeline(ctx).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A handler fault must not tear down the tunnel; surface a 500 so the
            // phone gets a definite answer and the loop keeps serving other ids.
            // The exception detail is deliberately not echoed over the tunnel.
            return Reject(request.Id, StatusCodes.Status500InternalServerError, "dispatch error");
        }

        return BuildResponse(request.Id, ctx, responseBody);
    }

    private static DefaultHttpContext BuildContext(
        RelayHttpRequest request, string phoneSessionId, IServiceProvider scopeServices, CancellationToken ct)
    {
        var ctx = new DefaultHttpContext { RequestServices = scopeServices };
        ctx.RequestAborted = ct;

        var req = ctx.Request;
        req.Method = NormalizeMethod(request.Method);
        var (pathOnly, query) = SplitPath(request.Path ?? string.Empty);
        req.Path = pathOnly;
        if (!string.IsNullOrEmpty(query))
            req.QueryString = new QueryString(query);
        req.Scheme = "https"; // relay is an HTTPS-equivalent secure channel; clears the plain-HTTP CSRF guard.

        if (request.Body is { Length: > 0 } body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            req.Body = new MemoryStream(bytes);
            req.ContentLength = bytes.Length;
            req.ContentType = string.IsNullOrEmpty(request.ContentType) ? ContentTypeJson : request.ContentType;
            // Minimal-API model binding only reads the request body when the
            // request reports it can have one via IHttpRequestBodyDetectionFeature.
            // A bare DefaultHttpContext has no such feature, so the binder treats
            // every body-bound POST over the tunnel as "no body" and 400s (the
            // bound parameter resolves null → required-body-missing) — which is
            // why relayed lighting effects, volume, etc. silently did nothing
            // while bodyless/route-param POSTs (cooling presets) worked. Declare
            // the body so binding reads it.
            ctx.Features.Set<IHttpRequestBodyDetectionFeature>(CanHaveBodyFeature.Instance);
        }

        // The authorization decision: act as this phone session, trusted because
        // the relay session is already authenticated. Both are server-side Items
        // a network request can never set; the marker is identity-checked.
        ctx.Items[TrustedRelayDispatchKey] = TrustedMarker;
        ctx.Items["PhoneSessionId"] = phoneSessionId;

        return ctx;
    }

    private static RelayHttpResponse BuildResponse(int id, HttpContext ctx, MemoryStream responseBody)
    {
        if (responseBody.Length > MaxBodyBytes)
        {
            // Refuse to seal an oversized response (cost / memory guard); the
            // panel sees a definite 413 instead of a truncated payload.
            return Reject(id, StatusCodes.Status413PayloadTooLarge, "response body exceeds relay cap");
        }

        var contentType = ctx.Response.ContentType;
        var bytes = responseBody.ToArray();

        // Text (JSON/HTML/...) rides as a plain UTF-8 string — the original wire
        // shape, so a panel that predates the Base64 flag still parses it. Only
        // binary (thumbnails, icons), which a UTF-8 round-trip through the JSON
        // frame would corrupt, is base64'd + flagged; updated panels decode it.
        // This keeps the encoding backward-compatible: an old client never sees
        // a base64 body where it expects text.
        if (IsTextContentType(contentType))
        {
            return new RelayHttpResponse
            {
                Id = id,
                Status = ctx.Response.StatusCode,
                Body = Encoding.UTF8.GetString(bytes),
                ContentType = contentType,
            };
        }

        return new RelayHttpResponse
        {
            Id = id,
            Status = ctx.Response.StatusCode,
            Body = Convert.ToBase64String(bytes),
            ContentType = contentType,
            Base64 = true,
        };
    }

    // Text rides as a plain UTF-8 string (backward-compatible); anything else is
    // treated as binary and base64'd. Null/empty ⇒ text — the panel's binary
    // routes always set an image/* content type.
    private static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return true;
        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("svg", StringComparison.OrdinalIgnoreCase);
    }

    private static RelayHttpResponse Reject(int id, int status, string message)
        => new()
        {
            Id = id,
            Status = status,
            Body = $"{{\"error\":true,\"msg\":\"{message}\"}}",
            ContentType = ContentTypeJson,
        };

    private static string NormalizeMethod(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return HttpMethods.Get;
        return method.ToUpperInvariant();
    }

    private static (string Path, string Query) SplitPath(string raw)
    {
        var q = raw.IndexOf('?');
        return q < 0 ? (raw, string.Empty) : (raw[..q], raw[q..]);
    }

    /// <summary>
    /// Tells minimal-API model binding the synthetic relay request carries a body.
    /// Without it, <c>RequestDelegateFactory</c> never reads the body off a bare
    /// <see cref="DefaultHttpContext"/> and 400s every body-bound POST.
    /// </summary>
    private sealed class CanHaveBodyFeature : IHttpRequestBodyDetectionFeature
    {
        public static readonly CanHaveBodyFeature Instance = new();
        public bool CanHaveBody => true;
    }
}
