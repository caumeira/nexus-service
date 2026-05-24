using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Engine;

public sealed class LightingEngine : IDisposable
{
    private readonly CanvasBuffer _canvas;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile IEffect? _currentEffect;
    private volatile DeviceFrame[] _devices = Array.Empty<DeviceFrame>();
    private byte[] _frameBuffer = Array.Empty<byte>();

    public LightingEngine() { _canvas = new CanvasBuffer(160, 90); }

    public int FrameIntervalMs { get; set; } = 33;
    public event Action<ReadOnlyMemory<byte>>? OnFrame;
    public string CurrentEffectName => _currentEffect?.Name ?? "none";
    public IEffect? CurrentEffect => _currentEffect;
    public DeviceFrame[] Devices => _devices;
    public void UpdateDevices(DeviceFrame[] devices) { _devices = devices; }

    public void SetEffect(IEffect effect)
    {
        lock (_lock)
        {
            var old = _currentEffect;
            _currentEffect = effect;
            try
            { old?.Dispose(); }
            catch { }
            if (_loopTask is null || _loopTask.IsCompleted)
            { _cts = new CancellationTokenSource(); _loopTask = Task.Run(() => RunLoopAsync(_cts.Token)); }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            var old = _currentEffect;
            _currentEffect = null;
            _cts?.Cancel();
            try
            { old?.Dispose(); }
            catch { }
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }

            try
            { SerializeAndBroadcast(); }
            catch { }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // PeriodicTimer allocates once per loop vs Task.Delay allocating a
        // fresh Task every frame. Period is reloaded each tick so live
        // FrameIntervalMs changes propagate without restarting the loop.
        var periodMs = Math.Max(1, FrameIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var effect = _currentEffect;
                if (effect is null)
                {
                    break;
                }

                try
                { effect.RenderFrame(_canvas, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); SampleDevicesFromCanvas(); SerializeAndBroadcast(); }
                catch (Exception ex) { Console.Error.WriteLine($"[lighting-engine] {effect.Name} threw: {ex.Message}"); }

                var nextPeriodMs = Math.Max(1, FrameIntervalMs);
                if (nextPeriodMs != periodMs)
                {
                    periodMs = nextPeriodMs;
                    timer.Period = TimeSpan.FromMilliseconds(periodMs);
                }
                try
                {
                    if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _canvas.Clear();
            foreach (var dev in _devices)
            {
                dev.Clear();
            }
        }
    }

    private void SampleDevicesFromCanvas()
    {
        var devices = _devices;
        var cw = _canvas.Width;
        var ch = _canvas.Height;
        const float CW = 1000f, CH = 600f;
        foreach (var dev in devices)
        {
            var ledCount = dev.LedCount;
            if (ledCount <= 0)
            {
                continue;
            }

            var rectX = dev.X / CW * cw;
            var rectY = dev.Y / CH * ch;
            var rectW = dev.W / CW * cw;
            var rectH = dev.H / CH * ch;
            var rot = ((dev.Rotation % 360) + 360) % 360;

            // Keyboards and other matrix devices provide per-LED UVs so each key samples
            // from its real 2D position inside the rectangle instead of being stretched
            // along a single axis. Rotation is applied to the UV coordinates around the
            // rectangle centre so the user can reorient the board without the mapping
            // becoming nonsensical.
            var devLedU = dev.LedU;
            var devLedV = dev.LedV;
            var devLedDisabled = dev.LedDisabled;
            if (devLedU is not null && devLedV is not null
                && devLedU.Length == ledCount && devLedV.Length == ledCount)
            {
                for (int i = 0; i < ledCount; i++)
                {
                    if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                    {
                        dev.SetLed(i, 0, 0, 0);
                        continue;
                    }
                    var u = devLedU[i];
                    var v = devLedV[i];
                    float ur, vr;
                    switch (rot)
                    {
                        case 90:
                            ur = 1f - v;
                            vr = u;
                            break;
                        case 180:
                            ur = 1f - u;
                            vr = 1f - v;
                            break;
                        case 270:
                            ur = v;
                            vr = 1f - u;
                            break;
                        default:
                            ur = u;
                            vr = v;
                            break;
                    }
                    var px = (int)(rectX + ur * rectW);
                    var py = (int)(rectY + vr * rectH);
                    var (r, g, b) = _canvas.GetPixel(px, py);
                    dev.SetLed(i, r, g, b);
                }
                continue;
            }

            // Linear strip fallback: walk the LEDs along the rotation axis.
            var cx = rectX + rectW * 0.5f;
            var cy = rectY + rectH * 0.5f;
            float dx, dy;
            switch (rot)
            {
                case 90:
                    dx = 0f;
                    dy = rectH;
                    break;
                case 180:
                    dx = -rectW;
                    dy = 0f;
                    break;
                case 270:
                    dx = 0f;
                    dy = -rectH;
                    break;
                default:
                    dx = rectW;
                    dy = 0f;
                    break;
            }
            var denom = ledCount > 1 ? 1f / (ledCount - 1) : 0f;
            for (int i = 0; i < ledCount; i++)
            {
                if (devLedDisabled is not null && i < devLedDisabled.Length && devLedDisabled[i])
                {
                    dev.SetLed(i, 0, 0, 0);
                    continue;
                }
                var t = ledCount > 1 ? i * denom - 0.5f : 0f;
                var (r, g, b) = _canvas.GetPixel((int)(cx + t * dx), (int)(cy + t * dy));
                dev.SetLed(i, r, g, b);
            }
        }

        ApplyTestOverlays(devices);
    }

    private static void ApplyTestOverlays(DeviceFrame[] devices)
    {
        foreach (var dev in devices)
        {
            var highlights = dev.HighlightLeds;
            if (highlights is not null && highlights.Count > 0)
            {
                for (int i = 0; i < dev.LedCount; i++)
                {
                    if (highlights.Contains(i))
                    {
                        dev.SetLed(i, 255, 255, 255);
                    }
                    else
                    {
                        dev.SetLed(i, 0, 0, 0);
                    }
                }
                continue;
            }

            var pattern = dev.TestPattern;
            var ledU = dev.LedU;
            var ledV = dev.LedV;
            if (pattern is not null)
            {
                if (pattern == "none")
                {
                    dev.Fill(0, 0, 0);
                }
                else if (ledU is not null && ledV is not null)
                {
                    var elapsed = dev.TestPatternStartMs > 0
                        ? Environment.TickCount64 - dev.TestPatternStartMs
                        : 0L;
                    var phase = (float)(elapsed % 2000 / 2000.0);
                    for (int i = 0; i < dev.LedCount && i < ledU.Length && i < ledV.Length; i++)
                    {
                        var u = ledU[i];
                        var v = ledV[i];
                        float t = pattern == "horizontal" ? u : v;
                        var band = 1f - Math.Min(1f, Math.Abs(t - phase) * 5f);
                        dev.SetLed(i,
                            (byte)(band * 255), (byte)(band * 255), (byte)(band * 255));
                    }
                }
            }
        }
    }

    private void SerializeAndBroadcast()
    {
        var devices = _devices;
        var canvasBytes = _canvas.ByteCount;
        var totalSize = 1 + 4 + canvasBytes + 1;
        foreach (var dev in devices)
        {
            totalSize += 1 + 2 + dev.LedCount * 3;
        }

        if (_frameBuffer.Length < totalSize)
        {
            _frameBuffer = new byte[totalSize + 256];
        }

        var buf = _frameBuffer;
        buf[0] = 0x03;
        var pos = 1;
        var cw = (ushort)_canvas.Width;
        var ch = (ushort)_canvas.Height;
        buf[pos++] = (byte)(cw & 0xFF);
        buf[pos++] = (byte)(cw >> 8);
        buf[pos++] = (byte)(ch & 0xFF);
        buf[pos++] = (byte)(ch >> 8);
        _canvas.Pixels.CopyTo(buf.AsSpan(pos));
        pos += canvasBytes;
        buf[pos++] = (byte)Math.Min(devices.Length, 255);
        foreach (var dev in devices)
        {
            buf[pos++] = (byte)dev.Index;
            var lc = (ushort)dev.LedCount;
            buf[pos++] = (byte)(lc & 0xFF);
            buf[pos++] = (byte)(lc >> 8);
            dev.LedBytes.CopyTo(buf.AsSpan(pos));
            pos += dev.LedCount * 3;
        }
        OnFrame?.Invoke(new ReadOnlyMemory<byte>(_frameBuffer, 0, pos));
    }

    public void Dispose() { Stop(); _cts?.Dispose(); }
}
