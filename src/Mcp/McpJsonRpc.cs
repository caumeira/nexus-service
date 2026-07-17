using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nexus.Service.Mcp;

/// <summary>
/// Writes JSON-RPC 2.0 envelopes for the /mcp endpoint via Utf8JsonWriter (no
/// reflection JSON). Every response - success or JSON-RPC error - goes back as
/// a single application/json body with HTTP 200; only pre-JSON-RPC transport
/// checks (origin, auth, protocol version, method) use other HTTP status codes.
/// </summary>
internal static class McpJsonRpc
{
    public static async Task WriteResultAsync(HttpContext ctx, JsonElement id, Action<Utf8JsonWriter> writeResult)
    {
        ctx.Response.ContentType = "application/json";
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            writeResult(writer);
            writer.WriteEndObject();
        }
        stream.Position = 0;
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    public static async Task WriteErrorAsync(HttpContext ctx, JsonElement? id, int code, string message)
    {
        ctx.Response.ContentType = "application/json";
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is { } idValue)
            {
                idValue.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        stream.Position = 0;
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }
}
