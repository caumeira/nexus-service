using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
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
    private const int PollMs = 1000;

    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private readonly HardwarePresence _presence;
    private readonly KeebLightingDeviceProvider? _lighting;
    private bool _lastConnected;

    public KeebConnectionWorker(KeebHub hub, KeebSettingsApplier applier, HardwarePresence presence, KeebLightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _applier = applier;
        _presence = presence;
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
        // Skip silently when disconnected and no keeb is on the bus; stay live
        // while connected so an unplug is still noticed and broadcast below.
        if (!_hub.IsConnected && !_presence.UsbPresent(KeebProtocol.VendorId, KeebProtocol.ProductId))
            return;

        _hub.EnsureConnected();
        var connected = _hub.IsConnected;
        if (connected != _lastConnected)
        {
            _lastConnected = connected;
            // On (re)connect, read device info (firmware version + layout), sync the
            // device's current firmware effect + brightness into persisted state,
            // then push the saved settings so game mode / rotary / animation take
            // effect immediately.
            if (connected)
            {
                _hub.ReadDeviceInfo();
                _applier.SyncFromDevice();
                _applier.Apply();
            }
            _lighting?.OnConnectionChanged();
            return;
        }
        // While connected, poll the device's firmware effect + brightness each tick
        // so the panel and software stream follow a hardware-side change. In FIRMWARE
        // rotary mode the middle button cycles the effect and the knob moves the
        // brightness byte with NO host callback (the EP2 roller callbacks, hyte-refs
        // Keeb/9-callback.md, only fire in SOFTWARE rotary mode), so a periodic
        // settings read is the only way to observe either.
        if (connected) _applier.SyncFromDevice();
    }
}
