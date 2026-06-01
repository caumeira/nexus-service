using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the keeb over raw HID. Mirrors
/// <see cref="Np50LightingFrameWriter"/>: a 30 Hz timer walks
/// <see cref="LightingEngine.Devices"/>, picks the keeb keys + underglow
/// frames, applies brightness / disabled / identify against the shared
/// settings store, and streams each zone via <see cref="KeebHub"/>.
/// </summary>
public sealed class KeebLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33; // 30 Hz, matches the engine + NP50 writer.
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly KeebSettingsApplier _applier;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;

    private RgbColor[] _keyBuf = new RgbColor[KeebLayout.KeyLedCount];
    private RgbColor[] _surroundBuf = new RgbColor[KeebLayout.SurroundLedCount];

    public KeebLightingFrameWriter(LightingEngine engine, KeebHub hub, IConfigStore store, Np50IdentifyTracker identify, KeebSettingsApplier applier)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _applier = applier;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown best-effort */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[keeb-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_hub.IsConnected) return;

        // Firmware/software arbitration: stream only while a software effect is
        // active. When it stops, re-assert the persisted firmware settings once
        // so the onboard animation (which streaming suppressed) comes back —
        // matching the panel's "firmware lighting applies when nexus isn't
        // actively driving the LEDs".
        if (_engine.CurrentEffectName == "none")
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                _applier.Apply();
            }
            return;
        }
        _wasStreaming = true;

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var hubId = _hub.DeviceId;
        if (string.IsNullOrEmpty(hubId)) return;
        var keysId = hubId + KeebLightingDeviceProvider.KeysSuffix;
        var underglowId = hubId + KeebLightingDeviceProvider.UnderglowSuffix;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        DeviceFrame? keys = null;
        DeviceFrame? underglow = null;
        for (var i = 0; i < devices.Length; i++)
        {
            var d = devices[i];
            if (d.Id == keysId) keys = d;
            else if (d.Id == underglowId) underglow = d;
        }

        if (keys is not null)
        {
            EnsureBuf(ref _keyBuf, KeebLayout.KeyLedCount);
            FillZone(_keyBuf, keys, disabled, prefs, globalBrightness, nowTicks);
            _hub.WriteKeyboard(_keyBuf);
        }
        if (underglow is not null)
        {
            EnsureBuf(ref _surroundBuf, KeebLayout.SurroundLedCount);
            FillZone(_surroundBuf, underglow, disabled, prefs, globalBrightness, nowTicks);
            _hub.WriteSurround(_surroundBuf);
        }
    }

    private void FillZone(RgbColor[] dst, DeviceFrame frame,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var ledCount = Math.Min(dst.Length, frame.LedCount);
        var mul = ComputeBrightnessMul(frame.Id, disabled, prefs, globalBrightness);
        if (_identify.TryGetActive(frame.Id, nowTicks, out var startTicks))
        {
            var elapsedMs = (nowTicks - startTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new RgbColor(255, 255, 255) : default;
            for (var i = 0; i < ledCount; i++) dst[i] = c;
            for (var i = ledCount; i < dst.Length; i++) dst[i] = default;
            return;
        }
        var src = frame.LedBytes;
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) { dst[i] = default; continue; }
            if (mul >= 0.999)
                dst[i] = new RgbColor(src[off], src[off + 1], src[off + 2]);
            else if (mul <= 0.0)
                dst[i] = default;
            else
                dst[i] = new RgbColor((byte)(src[off] * mul), (byte)(src[off + 1] * mul), (byte)(src[off + 2] * mul));
        }
        for (var i = ledCount; i < dst.Length; i++) dst[i] = default;
    }

    private static double ComputeBrightnessMul(string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness)
    {
        for (var i = 0; i < disabled.Count; i++) if (disabled[i] == id) return 0.0;
        int devBrightness;
        try { devBrightness = prefs.TryGetValue(id, out var pref) ? pref.Brightness : 100; }
        catch (InvalidOperationException) { devBrightness = 100; }
        return globalBrightness * Math.Clamp(devBrightness, 0, 100) / 100.0;
    }

    private static void EnsureBuf(ref RgbColor[] buf, int len)
    {
        if (buf.Length < len) buf = new RgbColor[len];
    }
}
