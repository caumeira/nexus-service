using System.Buffers.Binary;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Routes;

/// <summary>
/// Desktop-only bench/dev surface for the Phase 0 Stream Deck bring-up. Every
/// route is LocalhostOnly (matches plan streamdeck-support.md §5.5: a physical
/// deck is a desktop configuration surface, not something panels touch). The
/// full binding/config/image-upload routes land in Phase 1.
/// </summary>
public static class StreamDeckRoutes
{
    public static void MapStreamDeckEndpoints(this WebApplication app)
    {
        app.MapGet("/streamdeck/decks", (StreamDeckConnectionWorker worker) =>
        {
            var response = new GetStreamDecksResponse();
            foreach (var (key, surface) in worker.Surfaces)
            {
                response.Decks.Add(new StreamDeckSummaryDto
                {
                    Serial = surface.Serial,
                    Model = surface.Model.Name,
                    Rows = surface.Model.Rows,
                    Columns = surface.Model.Columns,
                    Connected = surface.IsConnected,
                    Verified = surface.Model.Verified,
                    Simulated = key == StreamDeckConnectionWorker.SimulatedKey,
                    FirmwareVersion = surface.FirmwareVersion,
                });
            }
            return response;
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/brightness", (string serial, SetStreamDeckBrightnessBody body, StreamDeckConnectionWorker worker) =>
        {
            var surface = worker.FindBySerial(serial);
            if (surface is null)
            {
                return ApiResponse.Fail("deck not found");
            }
            return surface.SetBrightness(body.Percent) ? ApiResponse.Ok() : ApiResponse.Fail("brightness write failed");
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
    /// solid-color/arbitrary-size encoder lives behind DEV_TOOLS-gated
    /// bench tooling, never a general-purpose capability.
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
