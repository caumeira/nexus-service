using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nexus.Service.Sockets;

/// <summary>
/// Builds <c>{"t":"topic","d":{ ... }}</c> envelopes for the multiplexed WebSocket.
/// Source-generated <see cref="JsonTypeInfo{T}"/> writes the payload directly
/// into the outer envelope writer | fully AOT-safe, no reflection, no
/// polymorphism. Serialization runs into an ArrayPool-backed scratch buffer, so
/// the doubling-growth arrays never become garbage; only the returned
/// right-sized copy is allocated (the broadcast holds it until it finishes
/// sending).
/// </summary>
public static class WsEnvelope
{
    public static ReadOnlyMemory<byte> Build<T>(string topic, T payload, JsonTypeInfo<T> typeInfo)
    {
        using var buffer = new PooledBufferWriter(1024);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writer.WriteString("t", topic);
        writer.WritePropertyName("d");
        JsonSerializer.Serialize(writer, payload, typeInfo);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    public static ReadOnlyMemory<byte> Wrap(string topic, ReadOnlySpan<byte> payloadJson)
    {
        using var buffer = new PooledBufferWriter(payloadJson.Length + topic.Length + 32);
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = true });

        writer.WriteStartObject();
        writer.WriteString("t", topic);
        writer.WritePropertyName("d");
        writer.WriteRawValue(payloadJson);
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// IBufferWriter over an ArrayPool-rented array. The scratch is returned to
    /// the pool on Dispose; callers copy out (<c>WrittenSpan.ToArray()</c>)
    /// before disposing.
    /// </summary>
    private sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _written;

        public PooledBufferWriter(int initial) => _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initial, 256));

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
        public void Advance(int count) => _written += count;
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(_written); }

        private void Ensure(int sizeHint)
        {
            if (sizeHint < 1) sizeHint = 1;
            if (_buffer.Length - _written >= sizeHint) return;
            int next = Math.Max(_buffer.Length * 2, _written + sizeHint);
            var grown = ArrayPool<byte>.Shared.Rent(next);
            Array.Copy(_buffer, grown, _written);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        public void Dispose()
        {
            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = Array.Empty<byte>();
            }
        }
    }
}
