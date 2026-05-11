using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Qos.Service.Sockets;

/// <summary>
/// Builds <c>{"t":"topic","d":{ ... }}</c> envelopes for the multiplexed WebSocket.
/// Source-generated <see cref="JsonTypeInfo{T}"/> writes the payload directly
/// into the outer envelope writer | fully AOT-safe, no reflection, no
/// polymorphism. The buffer's underlying array is held by the returned
/// <see cref="ReadOnlyMemory{T}"/> until the broadcast finishes consuming it,
/// so we hand it back without an intermediate copy.
/// </summary>
public static class WsEnvelope
{
    public static ReadOnlyMemory<byte> Build<T>(string topic, T payload, JsonTypeInfo<T> typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writer.WriteString("t", topic);
        writer.WritePropertyName("d");
        JsonSerializer.Serialize(writer, payload, typeInfo);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenMemory;
    }

    public static ReadOnlyMemory<byte> Wrap(string topic, ReadOnlySpan<byte> payloadJson)
    {
        var buffer = new ArrayBufferWriter<byte>(payloadJson.Length + topic.Length + 32);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writer.WriteString("t", topic);
        writer.WritePropertyName("d");
        writer.WriteRawValue(payloadJson);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenMemory;
    }
}
