using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Reads the keeb's interrupt-IN callbacks (key-matrix presses, rotary scroll,
/// software keys, profile changes) and surfaces them. Opens its OWN read handle
/// to the vendor interface, independent of <see cref="KeebHub"/>'s write handle,
/// so the blocking read loop never contends with the 30 Hz RGB stream.
///
/// Logs decoded events: the device-key → physical (row,col) mapping can be
/// observed on the bench to reconcile the key-assignment grid.
/// </summary>
public sealed class KeebInputWorker : BackgroundService
{
    private const int ReadTimeoutMs = 200;
    private const int RetryDelayMs = 1000;
    // Per-detent step while the rotary is host-driven (software mode, i.e. a software
    // effect is streaming). Brightness is the keeb master onto global; volume mirrors
    // a media-key tap. Tunable to match the firmware's native feel.
    private const float GlobalStep = 0.04f;
    private const double VolumeStep = 0.02;

    private readonly IHidEnumerator _hid;
    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private readonly IConfigStore _store;
    private readonly IVolumeProvider _volume;
    private readonly MultiplexHub _panel;
    private IHidDevice? _reader;

    public KeebInputWorker(IHidEnumerator hid, KeebHub hub, KeebSettingsApplier applier,
        IConfigStore store, IVolumeProvider volume, MultiplexHub panel)
    {
        _hid = hid;
        _hub = hub;
        _applier = applier;
        _store = store;
        _volume = volume;
        _panel = panel;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => Loop(stoppingToken), stoppingToken);

    private async Task Loop(CancellationToken ct)
    {
        var buf = new byte[16];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_hub.IsConnected)
                {
                    CloseReader();
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }
                if (_reader is null && !OpenReader())
                {
                    await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                    continue;
                }

                var n = _reader!.Read(buf, ReadTimeoutMs);
                if (n == 0) continue; // idle: the read blocked up to ReadTimeoutMs, nothing arrived
                if (n < 0)
                {
                    // Device went away (unplug): the handle now fails reads
                    // instantly. Tear it down and back off so we don't spin a
                    // core; OpenReader re-acquires when the keeb returns.
                    CloseReader();
                    try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                var ev = KeebProtocol.ParseInputEvent(buf.AsSpan(0, n));
                if (ev.Kind != KeebProtocol.KeebInputKind.None) HandleEvent(ev);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb-input] read loop error: {ex.GetType().Name}: {ex.Message}");
                CloseReader();
                try { await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        CloseReader();
    }

    private void HandleEvent(KeebProtocol.KeebInputEvent ev)
    {
        // Key-matrix callbacks carry the firmware (row,col) for the pressed
        // key, used to map web grid cells onto firmware key indices.
        switch (ev.Kind)
        {
            case KeebProtocol.KeebInputKind.KeyMatrix:
                ServiceLog.Info($"[keeb-input] key matrix row={ev.Row} col={ev.Column}");
                break;
            case KeebProtocol.KeebInputKind.ScrollUp:
            case KeebProtocol.KeebInputKind.ScrollDown:
                HandleRotary(ev);
                break;
            case KeebProtocol.KeebInputKind.ScrollMiddle:
                // The middle button cycles the firmware effect on the device.
                // Re-read immediately so the panel's Effect selector follows
                // without waiting on the connection-worker poll.
                ServiceLog.Info("[keeb-input] rotary middle click");
                _applier.SyncFromDevice();
                break;
            case KeebProtocol.KeebInputKind.SoftwareKey:
                ServiceLog.Info($"[keeb-input] software key ap={ev.ApCode} pressed={ev.Pressed}");
                break;
            case KeebProtocol.KeebInputKind.Profile:
                ServiceLog.Info($"[keeb-input] profile -> {ev.Profile}");
                break;
        }
    }

    /// <summary>
    /// Rotary turn-events fire only while the keeb is in software mode (a software
    /// effect is streaming; see <see cref="KeebSettingsApplier.Apply"/>). The
    /// brightness encoder drives global brightness, the volume encoder mirrors a
    /// media-key volume tap. Mapping follows each encoder's configured function so a
    /// reassigned encoder still behaves; other functions are inert while streaming.
    /// </summary>
    private void HandleRotary(KeebProtocol.KeebInputEvent ev)
    {
        var up = ev.Kind == KeebProtocol.KeebInputKind.ScrollUp;
        var settings = _store.Load();
        var fn = (ev.Encoder == KeebProtocol.KeebEncoder.Left ? settings.Keeb.RotaryLeft : settings.Keeb.RotaryRight) ?? "";
        if (fn.Equals("BrightnessAdjustment", StringComparison.OrdinalIgnoreCase))
        {
            var next = 0f;
            _store.Update(s =>
            {
                next = Math.Clamp(s.Lighting.GlobalBrightness + (up ? GlobalStep : -GlobalStep), 0f, 1f);
                s.Lighting.GlobalBrightness = next;
            });
            PanelTopics.BroadcastLighting(_panel);
            ServiceLog.Info($"[keeb-input] knob -> global {(int)(next * 100)}%");
        }
        else if (fn.Equals("VolumeAdjustment", StringComparison.OrdinalIgnoreCase))
        {
            var st = _volume.GetState();
            if (!st.Supported) return;
            var v = Math.Clamp(st.Volume + (up ? VolumeStep : -VolumeStep), 0.0, 1.0);
            _volume.SetVolume(v);
            PanelTopics.BroadcastVolume(_panel);
        }
    }

    private bool OpenReader()
    {
        var info = KeebHub.FindVendorInterface(_hid);
        if (info is null) return false;
        _reader = _hid.Open(info.Path, forInput: true);
        if (_reader is null) return false;
        ServiceLog.Info($"[keeb-input] reader opened on {info.Path}");
        return true;
    }

    private void CloseReader()
    {
        try { _reader?.Dispose(); } catch { }
        _reader = null;
    }
}
