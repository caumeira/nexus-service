using System.Buffers.Binary;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Stream Deck REST contract (plan streamdeck-support.md §5.5). Every route
/// is LocalhostOnly - a physical deck is a desktop configuration surface, not
/// something a paired phone panel touches. test-pattern and the DEV_TOOLS
/// sim-press route are additive bench tooling, not part of the desktop
/// contract the web editor drives.
/// </summary>
public static class StreamDeckRoutes
{
    /// <summary>Key images are at most 96x96; a few hundred KB covers any BMP/JPEG encode with margin.</summary>
    private const long MaxImageBytes = 300_000;

    public static void MapStreamDeckEndpoints(this WebApplication app)
    {
        app.MapGet("/streamdeck/decks", (
            StreamDeckConnectionWorker worker, IConfigStore store, StreamDeckHandler handler, IUsbEnumerator usb) =>
        {
            var settings = store.Load().StreamDeck;
            var warning = handler.GetWarning(usb.Enumerate());
            var conflictAppId = warning is not null ? StreamDeckHandler.ElgatoConflictAppId : null;
            var response = new GetStreamDecksResponse();
            var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, surface) in worker.Surfaces)
            {
                seenSerials.Add(surface.Serial);
                settings.Decks.TryGetValue(surface.Serial, out var deck);
                response.Decks.Add(new StreamDeckSummaryDto
                {
                    Serial = surface.Serial,
                    Model = surface.Model.Name,
                    Name = string.IsNullOrEmpty(deck?.Name) ? surface.Model.Name : deck!.Name,
                    Connected = surface.IsConnected,
                    Verified = surface.Model.Verified,
                    Rows = surface.Model.Rows,
                    Columns = surface.Model.Columns,
                    KeyCount = surface.Model.KeyCount,
                    KeyPixels = surface.Model.KeyPixelSize,
                    Format = FormatName(surface.Model.ImageFormat),
                    Transform = surface.Model.Transform,
                    Brightness = deck?.Brightness ?? PhysicalDeckSettings.DefaultBrightness,
                    Orientation = deck?.Orientation ?? 0,
                    SleepAfterSeconds = deck?.SleepAfterSeconds ?? 0,
                    FirmwareVersion = surface.FirmwareVersion,
                    Warning = warning,
                    ConflictAppId = conflictAppId,
                });
            }

            // Persisted decks with no live surface (unplugged, or never seen
            // this run) still list so their name/config stay reachable.
            foreach (var (serial, deck) in settings.Decks)
            {
                if (seenSerials.Contains(serial))
                {
                    continue;
                }
                var model = StreamDeckModels.ByProductId(deck.ProductId);
                if (model is null)
                {
                    continue;
                }
                response.Decks.Add(new StreamDeckSummaryDto
                {
                    Serial = serial,
                    Model = model.Name,
                    Name = string.IsNullOrEmpty(deck.Name) ? model.Name : deck.Name,
                    Connected = false,
                    Verified = model.Verified,
                    Rows = model.Rows,
                    Columns = model.Columns,
                    KeyCount = model.KeyCount,
                    KeyPixels = model.KeyPixelSize,
                    Format = FormatName(model.ImageFormat),
                    Transform = model.Transform,
                    Brightness = deck.Brightness,
                    Orientation = deck.Orientation,
                    SleepAfterSeconds = deck.SleepAfterSeconds,
                    FirmwareVersion = "",
                    Warning = null,
                    ConflictAppId = null,
                });
            }
            return response;
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}", (
            string serial, UpdateStreamDeckBody body, StreamDeckConnectionWorker worker, IConfigStore store, MultiplexHub hub) =>
        {
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                if (body.Name is not null)
                {
                    deck.Name = body.Name;
                }
                if (body.Brightness is not null)
                {
                    deck.Brightness = Math.Clamp(body.Brightness.Value, 0, 100);
                }
                if (body.Orientation is not null)
                {
                    deck.Orientation = ClampOrientation(body.Orientation.Value);
                }
                if (body.SleepAfterSeconds is not null)
                {
                    deck.SleepAfterSeconds = Math.Max(0, body.SleepAfterSeconds.Value);
                }
            });
            // Skipped while the deck is asleep (sleep-after blanked it to
            // brightness 0): the new value already persisted above and
            // applies the moment the next key press wakes it.
            if (body.Brightness is not null && !worker.IsAsleep(serial))
            {
                worker.FindBySerial(serial)?.SetBrightness(Math.Clamp(body.Brightness.Value, 0, 100));
            }
            PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "decks", Serial = serial });
            return ApiResponse.Ok();
        }).LocalhostOnly();

        app.MapGet("/streamdeck/decks/{serial}/config", (string serial, IConfigStore store) =>
        {
            var settings = store.Load().StreamDeck;
            var config = settings.Decks.TryGetValue(serial, out var deck) ? deck.Deck : new DeckConfig();
            return new StreamDeckConfigEnvelope { Config = config };
        }).LocalhostOnly();

        app.MapPut("/streamdeck/decks/{serial}/config", (
            string serial, StreamDeckConfigEnvelope body, StreamDeckConnectionWorker worker, IConfigStore store, MultiplexHub hub) =>
        {
            var config = body.Config;
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                deck.Deck = config;
            });
            worker.RefreshView(serial);
            PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "config", Serial = serial });
            return new StreamDeckConfigEnvelope { Config = config };
        }).LocalhostOnly();

        app.MapPut("/streamdeck/decks/{serial}/images/{slotPath}/{state}", async (
            string serial, string slotPath, string state, HttpRequest req,
            StreamDeckImageCache cache, IConfigStore store, StreamDeckConnectionWorker worker, CancellationToken ct) =>
        {
            if (!StreamDeckImageCache.IsValidSerial(serial))
            {
                return Results.Json(ApiResponse.Fail("invalid serial"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (!IsValidImageSlotAndState(slotPath, state))
            {
                return Results.Json(ApiResponse.Fail("invalid slot path or state"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            var (bytes, tooLarge) = await ReadBoundedAsync(req.Body, MaxImageBytes, ct).ConfigureAwait(false);
            if (tooLarge)
            {
                return Results.Json(ApiResponse.Fail("image too large"), AppJsonContext.Default.ApiResponse);
            }
            if (bytes is null || bytes.Length == 0)
            {
                return Results.Json(ApiResponse.Fail("empty upload"), AppJsonContext.Default.ApiResponse);
            }

            var model = worker.FindBySerial(serial)?.Model;
            if (model is null && store.Load().StreamDeck.Decks.TryGetValue(serial, out var persistedDeck))
            {
                model = StreamDeckModels.ByProductId(persistedDeck.ProductId);
            }
            if (model is not null && !model.IsValidWireImageLength(bytes.Length))
            {
                return Results.Json(ApiResponse.Fail("image size does not match this deck's key format"), AppJsonContext.Default.ApiResponse);
            }

            var hash = StreamDeckImageCache.Hash(bytes);
            try
            {
                cache.Store(serial, hash, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ServiceLog.Warn($"[streamdeck] image cache write failed for {serial}: {ex.Message}");
                return Results.Json(ApiResponse.Fail("image cache write failed"), AppJsonContext.Default.ApiResponse);
            }
            string? evictHash = null;
            var refKey = $"{slotPath}/{state}";
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                if (deck.ImageRefs.TryGetValue(refKey, out var previousHash) &&
                    previousHash != hash &&
                    !deck.ImageRefs.Any(kv => kv.Key != refKey && kv.Value == previousHash))
                {
                    evictHash = previousHash;
                }
                deck.ImageRefs[refKey] = hash;
            });
            if (evictHash is not null)
            {
                cache.Evict(serial, evictHash);
            }
            worker.RefreshView(serial);
            return Results.Json(new StreamDeckImageUploadResponse { Hash = hash }, AppJsonContext.Default.StreamDeckImageUploadResponse);
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/test-press/{slotPath}", async (
            string serial, string slotPath, StreamDeckConnectionWorker worker, IConfigStore store,
            IDeckActionExecutor executor, CancellationToken ct) =>
        {
            var indices = DeckConfigNavigation.ParseSlotPath(slotPath);
            if (indices is null)
            {
                return ApiResponse.Fail("invalid slot path");
            }
            var settings = store.Load().StreamDeck;
            if (!settings.Decks.TryGetValue(serial, out var deck))
            {
                return ApiResponse.Fail("deck not found");
            }
            var slot = DeckConfigNavigation.ResolveSlot(deck.Deck, indices);
            if (slot?.Action is null)
            {
                return ApiResponse.Fail("slot has no action");
            }

            var folderPath = indices.GetRange(0, indices.Count - 1);
            var slotIndex = indices[^1];
            var latchKey = $"{serial}:{string.Join('.', folderPath)}:{slotIndex}";
            await executor.ExecuteAsync(slot.Action, serial, slotIndex, latchKey, ct).ConfigureAwait(false);

            // Best effort: only correct when the simulator's live folder view
            // already matches this slot's containing folder.
            if (worker.FindBySerial(serial) is SimulatedStreamDeckSurface simulated)
            {
                var physicalIndex = slotIndex + (folderPath.Count > 0 ? 1 : 0);
                simulated.Poke(physicalIndex, true);
                simulated.Poke(physicalIndex, false);
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/test-pattern", (string serial, StreamDeckConnectionWorker worker) =>
        {
            var surface = worker.FindBySerial(serial);
            if (surface is null)
            {
                return ApiResponse.Fail("deck not found");
            }
            if (surface.Model.ImageFormat != StreamDeckImageFormat.Bmp)
            {
                return ApiResponse.Fail("test pattern only supported for gen1 BMP models");
            }

            for (var i = 0; i < surface.Model.KeyCount; i++)
            {
                var (r, g, b) = TestPatternColors[i % TestPatternColors.Length];
                var bmp = BuildSolidBmp(surface.Model.KeyPixelSize, surface.Model.KeyPixelSize, r, g, b);
                if (!surface.SetKeyImage(i, bmp))
                {
                    return ApiResponse.Fail($"key {i} image push failed");
                }
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();

#if DEV_TOOLS
        app.MapPost("/streamdeck/dev/sim-press", (StreamDeckSimPressBody body, StreamDeckConnectionWorker worker) =>
        {
            if (worker.Surfaces.TryGetValue(StreamDeckConnectionWorker.SimulatedKey, out var sim)
                && sim is SimulatedStreamDeckSurface simulated)
            {
                simulated.Poke(body.KeyIndex, body.Pressed);
                return ApiResponse.Ok();
            }
            return ApiResponse.Fail("simulator not available");
        }).LocalhostOnly();
#endif
    }

    private static string FormatName(StreamDeckImageFormat format) => format switch
    {
        StreamDeckImageFormat.Bmp => "bmp",
        StreamDeckImageFormat.Jpeg => "jpeg",
        _ => "",
    };

    /// <summary>Normalizes any degree value to the nearest of 0, 90, 180, or 270 (wrapping at 360).</summary>
    private static int ClampOrientation(int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        return normalized switch
        {
            >= 45 and < 135 => 90,
            >= 135 and < 225 => 180,
            >= 225 and < 315 => 270,
            _ => 0,
        };
    }

    /// <summary>
    /// A valid ImageRefs key is either the reserved "back" folder-back-key
    /// slot with state "0" (StreamDeckConnectionWorker.BackSlotPath, only
    /// ever pushed with state 0), or a real slot path (DeckConfigNavigation's
    /// dot-joined index chain) with state "0" or "1" (off/on for a toggle).
    /// </summary>
    private static bool IsValidImageSlotAndState(string slotPath, string state)
    {
        if (state != "0" && state != "1")
        {
            return false;
        }
        if (slotPath == "back")
        {
            return state == "0";
        }
        return DeckConfigNavigation.ParseSlotPath(slotPath) is not null;
    }

    /// <summary>Reads a request body up to maxBytes, checking the running total after every chunk so a chunked upload (no Content-Length) never buffers unbounded memory before the size check runs.</summary>
    private static async Task<(byte[]? Bytes, bool TooLarge)> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return (null, true);
            }
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return (ms.ToArray(), false);
    }

    // Fixed, visually distinct hues so each key is identifiable on the bench.
    private static readonly (byte r, byte g, byte b)[] TestPatternColors =
    {
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0),
        (255, 0, 255), (0, 255, 255), (255, 128, 0), (128, 0, 255),
    };

    /// <summary>
    /// Hand-rolled fixed-header 24bpp BMP builder for the dev-only test
    /// pattern route. Deliberately kept local to this file (not shared with
    /// the production ClearKey path): the service's image pipeline stays
    /// web-rendered per plan streamdeck-support.md §5, so the only
    /// solid-color/arbitrary-size encoder lives behind this bench route,
    /// never a general-purpose capability.
    /// </summary>
    private static byte[] BuildSolidBmp(int width, int height, byte r, byte g, byte b)
    {
        var pixelBytes = width * height * 3;
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), (uint)pixelBytes);
        for (var i = 54; i < bmp.Length; i += 3)
        {
            bmp[i] = b;
            bmp[i + 1] = g;
            bmp[i + 2] = r;
        }
        return bmp;
    }
}
