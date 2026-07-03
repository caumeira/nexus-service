using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxPanoramaProtocolTests
{
    // Extracts the JSON body from a framed STATE-all or POST-config packet.
    private static string ExtractJson(byte[] frame)
    {
        // Destuff: 0x5B 0x01 -> 0x5A, 0x5B 0x02 -> 0x5B (skip outer 0x5A markers)
        var inner = new List<byte>();
        for (var i = 1; i < frame.Length - 1; i++)
        {
            if (frame[i] == 0x5B && i + 1 < frame.Length - 1)
            {
                inner.Add(frame[i + 1] == 0x01 ? (byte)0x5A : (byte)0x5B);
                i++;
            }
            else
            {
                inner.Add(frame[i]);
            }
        }
        // Skip LEN_HI LEN_LO and trailing CRC; body = inner[2..^1].
        var bodyBytes = inner.Skip(2).Take(inner.Count - 3).ToArray();
        var body = Encoding.UTF8.GetString(bodyBytes);
        // Body is HTTP-like headers + blank line + JSON.
        var jsonStart = body.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return jsonStart >= 0 ? body.Substring(jsonStart + 4) : body;
    }

    [Fact]
    public void BuildStateAll_no_arg_produces_valid_json_with_required_fields()
    {
        var frame = TryxPanoramaProtocol.BuildStateAll();
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Core objects present
        Assert.True(root.TryGetProperty("cpu", out var cpu));
        Assert.True(root.TryGetProperty("gpu", out _));
        Assert.True(root.TryGetProperty("memory", out _));
        Assert.True(root.TryGetProperty("motherboard", out var mobo));

        // New fields present with zero values in the zero-fill path
        Assert.True(cpu.TryGetProperty("usage", out var usage));
        Assert.Equal(0, usage.GetInt32());
        Assert.True(mobo.TryGetProperty("pchTemperature", out var pch));
        Assert.Equal(0, pch.GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out var ts));
        Assert.Equal(0, ts.GetInt64());
    }

    [Fact]
    public void BuildStateAll_with_sensor_json_uses_provided_json()
    {
        var sensorJson = "{\"cpu\":{\"load\":42,\"usage\":42,\"temperature\":75,\"speedAverage\":3600,\"power\":65,\"voltage\":1}," +
                         "\"gpu\":{\"load\":80,\"temperature\":68,\"fan\":0,\"speed\":1800,\"power\":200,\"voltage\":1}," +
                         "\"memory\":{\"total\":32,\"used\":16,\"load\":50,\"temperature\":0,\"speed\":0}," +
                         "\"disk\":{\"total\":0,\"used\":0,\"load\":0,\"activity\":0,\"temperature\":0,\"readSpeed\":0,\"writeSpeed\":0}," +
                         "\"fans\":[{\"onBoard\":true,\"type\":\"Fan\",\"name\":\"Fan CPU\",\"value\":0}]," +
                         "\"motherboard\":{\"temperature\":35,\"pchTemperature\":40}," +
                         "\"network\":{\"upload\":0,\"download\":0}," +
                         "\"timestamp\":1234567890000}";

        var frame = TryxPanoramaProtocol.BuildStateAll(sensorJson);
        var json = ExtractJson(frame);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(42, root.GetProperty("cpu").GetProperty("load").GetInt32());
        Assert.Equal(42, root.GetProperty("cpu").GetProperty("usage").GetInt32());
        Assert.Equal(75, root.GetProperty("cpu").GetProperty("temperature").GetInt32());
        Assert.Equal(1234567890000L, root.GetProperty("timestamp").GetInt64());
        Assert.Equal(40, root.GetProperty("motherboard").GetProperty("pchTemperature").GetInt32());
    }
}
