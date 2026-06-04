using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class PostHogSinkBodyTests
{
    private static JsonElement Body(params TelemetryEvent[] batch)
    {
        var buf = new ArrayBufferWriter<byte>();
        PostHogSink.WriteBody(buf, "phc_test", "install-1", batch);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buf.WrittenSpan)).RootElement;
    }

    [Fact]
    public void Event_carries_api_key_distinct_id_lib_and_props()
    {
        var ev = new TelemetryEvent
        {
            Name = "fan_speed_set",
            Timestamp = DateTimeOffset.UnixEpoch,
            Properties = new[] { new KeyValuePair<string, object?>("speed", 80) },
        };
        var root = Body(ev);

        Assert.Equal("phc_test", root.GetProperty("api_key").GetString());
        var e0 = root.GetProperty("batch")[0];
        Assert.Equal("fan_speed_set", e0.GetProperty("event").GetString());
        Assert.Equal("install-1", e0.GetProperty("distinct_id").GetString());

        var props = e0.GetProperty("properties");
        Assert.Equal("nexus-service", props.GetProperty("$lib").GetString());
        Assert.Equal(80, props.GetProperty("speed").GetInt32());
    }

    [Fact]
    public void Identify_writes_set_block_with_array_values()
    {
        var ev = new TelemetryEvent
        {
            Name = "$identify",
            Timestamp = DateTimeOffset.UnixEpoch,
            Set = new[]
            {
                new KeyValuePair<string, object?>("cpu", "Ryzen"),
                new KeyValuePair<string, object?>("devices", new[] { "Keeb", "Mouse" }),
            },
        };
        var set = Body(ev).GetProperty("batch")[0].GetProperty("properties").GetProperty("$set");

        Assert.Equal("Ryzen", set.GetProperty("cpu").GetString());
        var devices = set.GetProperty("devices");
        Assert.Equal(JsonValueKind.Array, devices.ValueKind);
        Assert.Equal(2, devices.GetArrayLength());
        Assert.Equal("Keeb", devices[0].GetString());
    }
}
