using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Migration;

/// <summary>
/// Builds a widget Config dictionary (Dictionary&lt;string, JsonElement&gt;,
/// the same wire shape as PanelWidgetDto.Config) via Utf8JsonWriter so no
/// reflection-bound JsonSerializer call is needed for arbitrary scalar
/// values under AOT. Property order matches write order.
/// </summary>
internal sealed class Nexus2ConfigBuilder
{
    private readonly MemoryStream _stream = new();
    private readonly Utf8JsonWriter _writer;

    public Nexus2ConfigBuilder()
    {
        _writer = new Utf8JsonWriter(_stream);
        _writer.WriteStartObject();
    }

    public Nexus2ConfigBuilder String(string name, string value)
    {
        _writer.WriteString(name, value);
        return this;
    }

    public Nexus2ConfigBuilder Bool(string name, bool value)
    {
        _writer.WriteBoolean(name, value);
        return this;
    }

    public Nexus2ConfigBuilder Number(string name, double value)
    {
        _writer.WriteNumber(name, value);
        return this;
    }

    public Nexus2ConfigBuilder Number(string name, int value)
    {
        _writer.WriteNumber(name, value);
        return this;
    }

    public Nexus2ConfigBuilder StartObject(string name)
    {
        _writer.WriteStartObject(name);
        return this;
    }

    public Nexus2ConfigBuilder EndObject()
    {
        _writer.WriteEndObject();
        return this;
    }

    public Dictionary<string, JsonElement> Build()
    {
        _writer.WriteEndObject();
        _writer.Flush();
        using var doc = JsonDocument.Parse(_stream.ToArray());
        var result = new Dictionary<string, JsonElement>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }
        return result;
    }
}
