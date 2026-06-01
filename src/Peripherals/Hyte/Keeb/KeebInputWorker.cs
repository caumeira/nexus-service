using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Peripherals.Hid;

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

    private readonly IHidEnumerator _hid;
    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private IHidDevice? _reader;

    public KeebInputWorker(IHidEnumerator hid, KeebHub hub, KeebSettingsApplier applier)
    {
        _hid = hid;
        _hub = hub;
        _applier = applier;
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
                if (n <= 0) continue; // idle/timeout

                var ev = KeebProtocol.ParseInputEvent(buf.AsSpan(0, n));
                if (ev.Kind != KeebProtocol.KeebInputKind.None) HandleEvent(ev);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[keeb-input] read loop error: {ex.GetType().Name}: {ex.Message}");
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
                Console.Error.WriteLine($"[keeb-input] key matrix row={ev.Row} col={ev.Column}");
                break;
            case KeebProtocol.KeebInputKind.ScrollUp:
            case KeebProtocol.KeebInputKind.ScrollDown:
                Console.Error.WriteLine($"[keeb-input] {ev.Encoder} encoder {ev.Kind}");
                break;
            case KeebProtocol.KeebInputKind.ScrollMiddle:
                // The middle button cycles the firmware effect on the device.
                // Re-read the effect immediately so the panel's Effect selector
                // follows without waiting on a poll.
                Console.Error.WriteLine("[keeb-input] rotary middle click");
                _applier.SyncEffectFromDevice();
                break;
            case KeebProtocol.KeebInputKind.SoftwareKey:
                Console.Error.WriteLine($"[keeb-input] software key ap={ev.ApCode} pressed={ev.Pressed}");
                break;
            case KeebProtocol.KeebInputKind.Profile:
                Console.Error.WriteLine($"[keeb-input] profile -> {ev.Profile}");
                break;
        }
    }

    private bool OpenReader()
    {
        var info = KeebHub.FindVendorInterface(_hid);
        if (info is null) return false;
        _reader = _hid.Open(info.Path);
        if (_reader is null) return false;
        Console.Error.WriteLine($"[keeb-input] reader opened on {info.Path}");
        return true;
    }

    private void CloseReader()
    {
        try { _reader?.Dispose(); } catch { }
        _reader = null;
    }
}
