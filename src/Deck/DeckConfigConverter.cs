using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Nexus.Service.Deck;

/// <summary>
/// Hand-written AOT-safe converter for <see cref="DeckConfig"/>. nexus-web
/// moved the deck grid from a single flat slot list to a pages list
/// (deckLayout.ts's normalizeDeckConfig); this converter accepts both the
/// current wire shape <c>{ "pages": [{ "slots": [...] }] }</c> and the
/// legacy pre-pagination shape <c>{ "slots": [...] }</c>, so a settings.json
/// written before pagination landed still loads, its slots wrapped into a
/// single page. Writes always emit the current "pages" shape.
///
/// Neither shape present, or a malformed page entry, falls back to one
/// empty page rather than throwing - JsonConfigStore.Load resets every
/// setting on any deserialization exception, so a malformed deck config
/// must not blast-radius the rest of the document (the same guard
/// DeckActionConverter applies to a malformed action node).
/// </summary>
public sealed class DeckConfigConverter : JsonConverter<DeckConfig>
{
    public override DeckConfig Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var config = new DeckConfig();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var pageEl in pagesEl.EnumerateArray())
                {
                    config.Pages.Add(ReadPage(pageEl, options));
                }
            }
            else if (root.TryGetProperty("slots", out var slotsEl) && slotsEl.ValueKind == JsonValueKind.Array)
            {
                config.Pages.Add(new DeckPage { Slots = slotsEl.Deserialize(GetTypeInfo<List<DeckSlot>>(options)) ?? new() });
            }
        }

        if (config.Pages.Count == 0)
        {
            config.Pages.Add(new DeckPage());
        }
        return config;
    }

    private static DeckPage ReadPage(JsonElement pageEl, JsonSerializerOptions options)
    {
        if (pageEl.ValueKind != JsonValueKind.Object)
        {
            return new DeckPage();
        }
        if (pageEl.TryGetProperty("slots", out var slotsEl) && slotsEl.ValueKind == JsonValueKind.Array)
        {
            return new DeckPage { Slots = slotsEl.Deserialize(GetTypeInfo<List<DeckSlot>>(options)) ?? new() };
        }
        return new DeckPage();
    }

    public override void Write(Utf8JsonWriter writer, DeckConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("pages");
        writer.WriteStartArray();
        foreach (var page in value.Pages)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("slots");
            JsonSerializer.Serialize(writer, page.Slots, GetTypeInfo<List<DeckSlot>>(options));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>(JsonSerializerOptions options) =>
        (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
