using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Opens the keeb's vendor HID interface at startup and keeps it open across
/// hot-plug. Unlike the NP50 hub the keeb needs no heartbeat to retain
/// control, so this worker only owns connect/reconnect + notifying the
/// lighting provider when the device appears or disappears (so the lighting
/// page + engine refresh). RGB frames are pushed separately by
/// <see cref="KeebLightingFrameWriter"/>.
/// </summary>
public sealed class KeebConnectionWorker : BackgroundService
{
    private const int PollMs = 2000;

    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private readonly KeebLightingDeviceProvider? _lighting;
    private bool _lastConnected;

    public KeebConnectionWorker(KeebHub hub, KeebSettingsApplier applier, KeebLightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _applier = applier;
        _lighting = lighting;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[keeb-conn] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    /// <summary>One connect/notify cycle. Public so debug routes can step it.</summary>
    public void Tick()
    {
        _hub.EnsureConnected();
        var connected = _hub.IsConnected;
        if (connected == _lastConnected) return;
        _lastConnected = connected;
        // On (re)connect, read device info (firmware version + layout), then push
        // the saved firmware settings so game mode / rotary / animation take effect
        // immediately. The frame writer streams over the animation while a software
        // effect is active.
        if (connected)
        {
            _hub.ReadDeviceInfo();
            _applier.Apply();
        }
        _lighting?.OnConnectionChanged();
    }
}
