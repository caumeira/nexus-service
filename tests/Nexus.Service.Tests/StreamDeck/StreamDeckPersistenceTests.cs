using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

public class StreamDeckPersistenceTests
{
    [Fact]
    public void NexusSettings_WithStreamDeckBindings_RoundTripsThroughPersistenceJsonContext()
    {
        var settings = new NexusSettings();
        settings.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
        {
            Name = "My Deck",
            Brightness = 42,
            Orientation = 180,
            SleepAfterSeconds = 90,
            Deck = new DeckConfig
            {
                Slots =
                {
                    new DeckSlot
                    {
                        Label = "Lock",
                        Color = "#ff0000",
                        Action = new DeckAction { Type = "power", PowerAction = "lock" },
                    },
                    new DeckSlot
                    {
                        Folder = new DeckFolder
                        {
                            Slots =
                            {
                                new DeckSlot
                                {
                                    Action = new DeckAction
                                    {
                                        Type = "toggle",
                                        State = new DeckToggleState { Kind = "mute" },
                                        On = new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "muteToggle" } },
                                        Off = new DeckAction { Type = "system", SystemAction = new DeckSystemAction { Op = "muteToggle" } },
                                    },
                                },
                            },
                        },
                    },
                    new DeckSlot
                    {
                        Action = new DeckAction
                        {
                            Type = "sequence",
                            Steps = new List<DeckSequenceStep>
                            {
                                new()
                                {
                                    Action = new DeckAction { Type = "nexus", NexusAction = new DeckNexusAction { Op = "y70Brightness", Value = 80 } },
                                    PressMs = 10,
                                    GapAfterMs = 20,
                                },
                            },
                        },
                    },
                },
            },
            ImageRefs = { ["0/0"] = "abc123" },
        };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var roundTripped = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings);

        Assert.NotNull(roundTripped);
        var deck = roundTripped!.StreamDeck.Decks["SERIAL-1"];
        Assert.Equal("My Deck", deck.Name);
        Assert.Equal(42, deck.Brightness);
        Assert.Equal(180, deck.Orientation);
        Assert.Equal(90, deck.SleepAfterSeconds);
        Assert.Equal("abc123", deck.ImageRefs["0/0"]);
        Assert.Equal(3, deck.Deck.Slots.Count);
        Assert.Equal("lock", deck.Deck.Slots[0].Action!.PowerAction);
        Assert.Equal("mute", deck.Deck.Slots[1].Folder!.Slots[0].Action!.State!.Kind);
        Assert.Equal("muteToggle", deck.Deck.Slots[1].Folder!.Slots[0].Action!.On!.SystemAction!.Op);
        Assert.Single(deck.Deck.Slots[2].Action!.Steps!);
        Assert.Equal(80, deck.Deck.Slots[2].Action!.Steps![0].Action.NexusAction!.Value);
        Assert.Equal(10, deck.Deck.Slots[2].Action!.Steps![0].PressMs);
    }

    [Fact]
    public void PhysicalDeckSettings_DefaultBrightness_Is60()
    {
        Assert.Equal(60, new PhysicalDeckSettings().Brightness);
        Assert.Equal(60, PhysicalDeckSettings.DefaultBrightness);
    }

    [Fact]
    public void PhysicalDeckSettings_OrientationAndSleepAfterSeconds_DefaultToZero()
    {
        var deck = new PhysicalDeckSettings();
        Assert.Equal(0, deck.Orientation);
        Assert.Equal(0, deck.SleepAfterSeconds);
    }
}

public class StreamDeckImageCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StreamDeckImageCache _cache;

    public StreamDeckImageCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-cache-" + Guid.NewGuid().ToString("N")[..8]);
        _cache = new StreamDeckImageCache(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Hash_IsLowercaseHexSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("hello deck");
        var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(expected, StreamDeckImageCache.Hash(bytes));
    }

    [Fact]
    public void StoreThenLoad_ReturnsTheSameBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var hash = StreamDeckImageCache.Hash(bytes);

        _cache.Store("SERIAL-1", hash, bytes);
        var loaded = _cache.Load("SERIAL-1", hash);

        Assert.Equal(bytes, loaded);
    }

    [Fact]
    public void Load_UnknownHash_ReturnsNull()
    {
        Assert.Null(_cache.Load("SERIAL-1", "0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public void Store_IsANoOpWhenTheHashAlreadyExists()
    {
        var bytes = new byte[] { 9, 9, 9 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);

        // Re-storing different bytes under the SAME hash must not overwrite -
        // content-addressed storage treats an existing hash as already correct.
        _cache.Store("SERIAL-1", hash, new byte[] { 1, 1, 1 });

        Assert.Equal(bytes, _cache.Load("SERIAL-1", hash));
    }

    [Fact]
    public void DifferentSerials_AreIsolated()
    {
        var bytes = new byte[] { 7, 7, 7 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);

        Assert.Null(_cache.Load("SERIAL-2", hash));
    }

    [Theory]
    [InlineData("A00DA431130Y9Y")]
    [InlineData("sd-1a2b3c4d")]
    [InlineData("sim-0001")]
    [InlineData("XL-SERIAL")]
    [InlineData("a")]
    public void IsValidSerial_AcceptsEveryObservedSerialShape(string serial)
    {
        Assert.True(StreamDeckImageCache.IsValidSerial(serial));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsValidSerial_RejectsTraversalAndEmptyPayloads(string? serial)
    {
        Assert.False(StreamDeckImageCache.IsValidSerial(serial));
    }

    [Fact]
    public void IsValidSerial_RejectsLongerThan64Chars()
    {
        Assert.False(StreamDeckImageCache.IsValidSerial(new string('a', 65)));
        Assert.True(StreamDeckImageCache.IsValidSerial(new string('a', 64)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    public void Store_WithTraversalSerial_WritesNothingOutsideTheCacheRoot(string serial)
    {
        var bytes = new byte[] { 1, 2, 3 };
        var hash = StreamDeckImageCache.Hash(bytes);

        _cache.Store(serial, hash, bytes);

        // The only thing on disk under the parent of _tempDir must still be
        // _tempDir itself (empty, since Store no-ops on an invalid serial) -
        // nothing escaped into a sibling or ancestor directory.
        var parent = Path.GetDirectoryName(_tempDir)!;
        Assert.False(Directory.Exists(Path.Combine(parent, "evil")));
        if (Directory.Exists(_tempDir))
        {
            Assert.Empty(Directory.GetFileSystemEntries(_tempDir));
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    public void Load_WithTraversalSerial_ReturnsNull(string serial)
    {
        Assert.Null(_cache.Load(serial, "0000000000000000000000000000000000000000000000000000000000000000"));
    }

    [Fact]
    public void Evict_RemovesTheCachedFile()
    {
        var bytes = new byte[] { 4, 5, 6 };
        var hash = StreamDeckImageCache.Hash(bytes);
        _cache.Store("SERIAL-1", hash, bytes);
        Assert.NotNull(_cache.Load("SERIAL-1", hash));

        _cache.Evict("SERIAL-1", hash);

        Assert.Null(_cache.Load("SERIAL-1", hash));
    }

    [Fact]
    public void Evict_UnknownHash_IsANoOp()
    {
        _cache.Evict("SERIAL-1", "0000000000000000000000000000000000000000000000000000000000000000");
    }
}

/// <summary>
/// A malformed DeckAction node anywhere in settings.json must not blast-radius
/// the rest of the document: JsonConfigStore.Load's catch-all resets EVERY
/// setting to defaults on any deserialization exception, so a throwing
/// converter for one field would silently wipe an unrelated user's whole
/// config. Exercises the real JsonConfigStore (see JsonConfigStoreCorruptLoadTests),
/// not a hand-written fake.
/// </summary>
public sealed class DeckActionSettingsLoadSurvivalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public DeckActionSettingsLoadSurvivalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nexus-deckaction-survival-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MalformedDeckActionInASlot_DoesNotResetTheRestOfSettings()
    {
        var json = "{"
            + "\"schemaVersion\":11,"
            + "\"lighting\":{\"globalBrightness\":0.42},"
            + "\"streamDeck\":{\"decks\":{\"SERIAL-1\":{\"deck\":{\"slots\":[{\"action\":\"not an object\"}]}}}}"
            + "}";
        File.WriteAllText(_path, json);

        using var store = new JsonConfigStore(_path);
        var settings = store.Load();

        // The rest of the document survived (proves Load did not treat this
        // as corrupt and reset to defaults).
        Assert.Equal(0.42f, settings.Lighting.GlobalBrightness);
        Assert.False(File.Exists(_path + ".corrupt"));

        var slot = settings.StreamDeck.Decks["SERIAL-1"].Deck.Slots[0];
        Assert.NotNull(slot.Action);
        Assert.Equal("", slot.Action!.Type);
    }
}
