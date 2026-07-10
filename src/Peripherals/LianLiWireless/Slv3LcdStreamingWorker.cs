using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Streams each attached SL-LCD Wireless screen's configured content. Runs one
/// independent loop per discovered serial (started on attach, cancelled on
/// detach by <see cref="Slv3LcdConnectionWorker"/>'s discovery), since each
/// screen's animation has its own frame cadence and a single shared loop
/// cannot pace three screens with different GIF/video delays at once.
/// </summary>
public sealed class Slv3LcdStreamingWorker : BackgroundService
{
    private const int DiscoveryPollMs = 2000;
    private const int IdlePollMs = 1000;
    /// <summary>~1 fps: a sensor/clock frame only needs to look live, not smooth.</summary>
    private const int SensorClockPollMs = 1000;

    private readonly Slv3LcdHub _hub;
    private readonly Slv3Hub _rfHub;
    private readonly Slv3LcdMediaLibrary _library;
    private readonly Slv3LcdSensorReader _sensorReader;
    private readonly IConfigStore _store;
    private readonly DeviceControlGate _gate;
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    public Slv3LcdStreamingWorker(Slv3LcdHub hub, Slv3Hub rfHub, Slv3LcdMediaLibrary library, Slv3LcdSensorReader sensorReader, IConfigStore store, DeviceControlGate gate)
    {
        _hub = hub;
        _rfHub = rfHub;
        _library = library;
        _sensorReader = sensorReader;
        _store = store;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_gate.IsEnabled("lianli-wireless"))
                    {
                        StopAll();
                    }
                    else
                    {
                        ReconcileScreenLoops(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ServiceLog.Error($"[lianli-wireless-lcd] streaming worker error: {ex.Message}");
                }

                await Task.Delay(DiscoveryPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // The delay above throws on the same cancellation that ends this
            // loop, so StopAll must run here rather than after the loop to
            // guarantee every per-screen CancellationTokenSource is disposed.
            StopAll();
        }
    }

    private void ReconcileScreenLoops(CancellationToken stoppingToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in _hub.Discover())
        {
            if (string.IsNullOrWhiteSpace(port.Serial))
            {
                continue;
            }
            seen.Add(port.Serial);
            if (_running.ContainsKey(port.Serial))
            {
                continue;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[port.Serial] = cts;
            _ = RunScreenLoopAsync(port.Serial, cts.Token);
        }

        foreach (var serial in new List<string>(_running.Keys))
        {
            if (!seen.Contains(serial))
            {
                StopScreen(serial);
            }
        }
    }

    private void StopScreen(string serial)
    {
        if (!_running.Remove(serial, out var cts))
        {
            return;
        }
        cts.Cancel();
        cts.Dispose();
    }

    private void StopAll()
    {
        foreach (var serial in new List<string>(_running.Keys))
        {
            StopScreen(serial);
        }
    }

    /// <summary>
    /// One screen's lifetime loop: reapplies brightness/rotation/content on
    /// change and streams the active content. A fresh call (attach after
    /// detach) starts with sentinel "applied" values, so a static image
    /// re-pushes automatically on reconnect without special-casing it.
    /// </summary>
    private async Task RunScreenLoopAsync(string serial, CancellationToken ct)
    {
        string? loadedMediaId = null;
        Slv3LcdImageData? loadedImage = null;
        var appliedBrightness = -1;
        var appliedRotation = -1;
        var appliedContentType = "";
        var pushedStatic = false;
        // Animation phase resets on every (re)connect and content-type switch;
        // continuity across a reconnect is not required.
        var animationStartUtc = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Arm the RF TX for LCD video traffic before pushing frames
                // (L-Connect's ensure_video_mode); idempotent per RF connection,
                // so this also re-arms after a dongle reconnect. The screens'
                // video RF shares the 2.4 GHz air with the fan control link.
                _rfHub.EnsureVideoMode();

                var settings = _store.Load().Devices.LianLiWireless.Screens.TryGetValue(serial, out var s)
                    ? s
                    : new LianLiWirelessScreenSettings();

                if (settings.Brightness != appliedBrightness)
                {
                    _hub.SetBrightness(serial, settings.Brightness);
                    appliedBrightness = settings.Brightness;
                }
                if (settings.Rotation != appliedRotation)
                {
                    _hub.SetRotation(serial, settings.Rotation);
                    appliedRotation = settings.Rotation;
                }

                if (settings.MediaId != loadedMediaId)
                {
                    loadedMediaId = settings.MediaId;
                    loadedImage = loadedMediaId is null ? null : _library.LoadFrames(loadedMediaId);
                    pushedStatic = false;
                }
                if (settings.ContentType != appliedContentType)
                {
                    appliedContentType = settings.ContentType;
                    pushedStatic = false;
                    animationStartUtc = DateTime.UtcNow;
                }

                switch (settings.ContentType)
                {
                    case "image":
                    case "gif":
                    case "video":
                        if (loadedImage is null)
                        {
                            await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                            continue;
                        }
                        if (loadedImage.Frames.Length == 1)
                        {
                            if (!pushedStatic)
                            {
                                pushedStatic = _hub.PushImageToSerial(serial, loadedImage.Frames[0].JpegBytes);
                            }
                            await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                            continue;
                        }
                        await StreamAnimatedAsync(serial, loadedImage, ct).ConfigureAwait(false);
                        break;

                    case "sensor":
                    {
                        var reading = _sensorReader.Read(settings.SensorSource, settings.TempUnit);
                        var jpeg = Slv3LcdSensorRenderer.Render(
                            settings.SensorStyle, reading.Value, reading.Min, reading.Max, reading.Label, reading.Unit,
                            settings.ColorA, settings.ColorB);
                        _hub.PushImageToSerial(serial, jpeg);
                        await Task.Delay(SensorClockPollMs, ct).ConfigureAwait(false);
                        break;
                    }

                    case "clock":
                    {
                        var jpeg = Slv3LcdClockRenderer.Render(settings.ClockFace, DateTime.Now, settings.ColorA, settings.ColorB);
                        _hub.PushImageToSerial(serial, jpeg);
                        await Task.Delay(SensorClockPollMs, ct).ConfigureAwait(false);
                        break;
                    }

                    case "animation":
                    {
                        var elapsed = (DateTime.UtcNow - animationStartUtc).TotalSeconds;
                        var jpeg = Slv3LcdAnimationRenderer.Render(settings.AnimationId, elapsed, settings.ColorA, settings.ColorB);
                        _hub.PushImageToSerial(serial, jpeg);
                        await Task.Delay(Slv3LcdAnimationRenderer.FrameIntervalMs, ct).ConfigureAwait(false);
                        break;
                    }

                    default:
                        await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-wireless-lcd] stream error for {serial}: {ex.Message}");
                await Task.Delay(IdlePollMs, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task StreamAnimatedAsync(string serial, Slv3LcdImageData image, CancellationToken ct)
    {
        foreach (var frame in image.Frames)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            _hub.PushImageToSerial(serial, frame.JpegBytes);
            await Task.Delay(Math.Max(10, frame.DelayMs), ct).ConfigureAwait(false);
        }
    }
}
