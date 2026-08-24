using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Nexus.Service.Discord;

namespace Nexus.Service.Tests;

public class DiscordRichPresenceTests
{
    [Fact]
    public void Normalize_KeepsAKnownPreset()
    {
        Assert.Equal("Watching temps", DiscordRichPresence.Normalize("Watching temps"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("watching temps")]
    [InlineData("something a hand-edited settings file put here")]
    public void Normalize_FallsBackForAnythingUnrecognized(string? preset)
    {
        Assert.Equal(DiscordRichPresence.Default, DiscordRichPresence.Normalize(preset));
    }

    [Fact]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("Just vibin'", DiscordRichPresence.Normalize("  Just vibin'  "));
    }

    [Fact]
    public void WriteActivity_EmitsTheFieldsDiscordRenders()
    {
        var root = WriteActivityDocument("Tuning my rig", 1_700_000_000);

        Assert.Equal("Tuning my rig", root.GetProperty("details").GetString());
        Assert.Equal(1_700_000_000, root.GetProperty("timestamps").GetProperty("start").GetInt64());
        Assert.Equal(DiscordRichPresence.LargeImageKey, root.GetProperty("assets").GetProperty("large_image").GetString());

        var button = Assert.Single(root.GetProperty("buttons").EnumerateArray().ToList());
        Assert.Equal("https://hellonexus.com", button.GetProperty("url").GetString());
    }

    [Fact]
    public void WriteActivity_NormalizesTheDetailLine()
    {
        // A preset retired in a later build must not reach the wire verbatim.
        var root = WriteActivityDocument("Playing something else", 0);

        Assert.Equal(DiscordRichPresence.Default, root.GetProperty("details").GetString());
    }

    [Fact]
    public void BuildSetActivityPayload_CarriesThePidAndNonce()
    {
        var payload = DiscordIpcConnection.BuildSetActivityPayload(
            writer => DiscordRichPresence.WriteActivity(writer, "Watching temps", 42),
            processId: 1234,
            nonce: "test-nonce");

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        Assert.Equal("test-nonce", root.GetProperty("nonce").GetString());
        Assert.Equal(1234, root.GetProperty("args").GetProperty("pid").GetInt32());
        Assert.Equal("Watching temps", root.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());
    }

    [Fact]
    public void BuildSetActivityPayload_ClearsWithAnExplicitNullActivity()
    {
        // Omitting the key leaves the previous status up; only an explicit
        // null takes it down.
        var payload = DiscordIpcConnection.BuildSetActivityPayload(null, processId: 1, nonce: "n");

        using var document = JsonDocument.Parse(payload);
        var activity = document.RootElement.GetProperty("args").GetProperty("activity");

        Assert.Equal(JsonValueKind.Null, activity.ValueKind);
    }

    [Fact]
    public void EncodeFrame_PrefixesLittleEndianOpcodeAndLength()
    {
        var payload = Encoding.UTF8.GetBytes("{\"v\":1}");

        var frame = DiscordIpcConnection.EncodeFrame(0, payload);

        Assert.Equal(8 + payload.Length, frame.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(payload.Length, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(payload, frame[8..]);
    }

    [Fact]
    public void UnixSocketDirectories_ProbesTheRuntimeDirAndItsSandboxedSubdirectories()
    {
        var directories = DiscordIpcConnection.UnixSocketDirectories().ToList();

        // The bare runtime dir must come first: it is where a normal desktop
        // install lands, and the Flatpak/snap paths never exist on macOS.
        Assert.NotEmpty(directories);
        Assert.Contains(directories, d => d.EndsWith("com.discordapp.Discord", StringComparison.Ordinal));
        Assert.Contains(directories, d => d.EndsWith("snap.discord", StringComparison.Ordinal));
        Assert.Equal(directories.Count, directories.Distinct(StringComparer.Ordinal).Count());
    }

    private static JsonElement WriteActivityDocument(string preset, long startUnixSeconds)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            DiscordRichPresence.WriteActivity(writer, preset, startUnixSeconds);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }
}
