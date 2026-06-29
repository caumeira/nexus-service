using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Streams 480x480 JPEG frames to the attached LCD display. Reads settings on
/// each loop pass to pick up media, brightness, and rotation changes immediately.
/// </summary>
public sealed class CorsairLinkLcdWorker : BackgroundService
{
    private readonly CorsairLinkLcd _lcd;
    private readonly CorsairLinkLcdMediaLibrary _library;
    private readonly IConfigStore _store;
    private readonly CorsairLinkHub _hub;

    private string? _loadedMediaId;
    private LcdImageData? _loadedImage;
    // Sentinels ensure brightness and rotation are pushed on the first connected loop.
    private byte _appliedBrightness = 255;
    private byte _appliedRotation = 255;

    public CorsairLinkLcdWorker(
        CorsairLinkLcd lcd,
        CorsairLinkLcdMediaLibrary library,
        IConfigStore store,
        CorsairLinkHub hub)
    {
        _lcd = lcd;
        _library = library;
        _store = store;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_hub.State.IsConnected || !_hub.State.HasLcd)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (!_lcd.HasDevice)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                var s = _store.Load().Devices.Corsair;

                if (s.LcdBrightness != _appliedBrightness)
                {
                    _lcd.SetBrightness(s.LcdBrightness);
                    _appliedBrightness = s.LcdBrightness;
                }

                if (s.LcdRotation != _appliedRotation)
                {
                    _lcd.SetRotation(s.LcdRotation);
                    _appliedRotation = s.LcdRotation;
                }

                if (s.LcdSelectedMediaId is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                if (s.LcdSelectedMediaId != _loadedMediaId)
                {
                    _loadedImage = _library.LoadImageData(s.LcdSelectedMediaId);
                    _loadedMediaId = s.LcdSelectedMediaId;
                }

                if (_loadedImage is null)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    continue;
                }

                var image = _loadedImage;
                await StreamImageAsync(image, mem => _lcd.SendFrame(mem.Span), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[corsair-lcd] worker error: {ex.Message}");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Executes one image display cycle. Static images send one frame followed by
    /// the hardware keep-alive delay (lsh.go:5071). Animated images send all frames
    /// in order, each followed by its GIF delay clamped to the 10ms minimum.
    /// </summary>
    internal static async Task StreamImageAsync(
        LcdImageData image,
        Action<ReadOnlyMemory<byte>> sendFrame,
        CancellationToken ct)
    {
        if (image.Frames.Length == 1)
        {
            sendFrame(image.Frames[0].JpegBytes);
            // Firmware clears the panel if frames stop arriving; 10ms matches lsh.go:5071.
            await Task.Delay(10, ct).ConfigureAwait(false);
            return;
        }

        foreach (var frame in image.Frames)
        {
            if (ct.IsCancellationRequested) break;
            sendFrame(frame.JpegBytes);
            await Task.Delay(Math.Max(10, frame.DelayMs), ct).ConfigureAwait(false);
        }
    }
}
