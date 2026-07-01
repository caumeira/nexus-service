using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Cnvs;

/// <summary>
/// Background worker whose only job is to make <see cref="CnvsHub"/> the
/// first claimant of the CNVS COM port at service startup, beating
/// OpenRGB-headless to it. Without this, OpenRGB launches via
/// <c>OpenRgbProcessManager.Start</c> within a couple of seconds of the
/// service coming up and grabs COM7 - every subsequent CnvsHub write
/// then loses the race and returns <c>UnauthorizedAccessException</c>.
///
/// The worker ticks every 5 s. On hot-plug (CNVS unplugged + replugged
/// during a session) the timer re-grabs the port on the next tick. If OpenRGB
/// claims it first on a replug, LEDs go dark for one cycle until the worker
/// wins it back.
/// </summary>
public sealed class CnvsConnectionWorker : BackgroundService
{
    private const int InitialDelayMs = 100;
    private const int TickPeriodMs = 5000;

    private readonly CnvsHub _hub;
    // Optional: the lighting provider rebuilds its DeviceFrame when the hub
    // (re)connects. Injected so a hot-plug shows up on the lighting page
    // without a full service restart. Null in non-lighting test fixtures.
    private readonly CnvsLightingDeviceProvider? _lighting;
    // Source of truth for which firmware-settings bits the user has chosen.
    // We re-apply them on every (re)connect because the firmware appears to
    // accept FF DC 07 only when it's one of the first wire commands after
    // a USB connect (see CnvsHub.WriteSettings doc for the invariant).
    private readonly IConfigStore _store;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private bool _firstTick = true;

    public CnvsConnectionWorker(CnvsHub hub, IConfigStore store, HardwarePresence presence, DeviceControlGate gate, CnvsLightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _store = store;
        _presence = presence;
        _gate = gate;
        _lighting = lighting;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 100 ms head-start so we beat OpenRgbProcessManager.Start (which
        // runs from the same service-startup phase but doesn't kick in for
        // a few seconds after the hosted-service pipeline begins).
        try { await Task.Delay(InitialDelayMs, stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        try
        {
            do
            {
                try { Tick(); }
                catch (Exception ex) { ServiceLog.Error($"[cnvs-conn] tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }

        _hub.Disconnect();
    }

    private void Tick()
    {
        // Nexus Control gate takes priority over the first-tick race-and-hold:
        // a device toggled off must never be claimed, even transiently.
        if (!_gate.IsEnabled("cnvs"))
        {
            if (_hub.IsConnected) _hub.Disconnect();
            return;
        }

        // First tick stays ungated so the race-and-hold isn't delayed by a cold
        // USB-enumeration scan. After that, skip silently when disconnected and no
        // CNVS is on the bus.
        if (!_firstTick && !_hub.IsConnected && !_presence.UsbPresent(CnvsProtocol.VendorId, CnvsProtocol.ProductIds))
            return;
        _firstTick = false;

        _hub.EnsureConnected();
        if (!_hub.IsConnected) return;

        // CNVS hardware serial is stable across USB power cycles, so we can't
        // dedup on it: a replug returns the same serial. Use the hub's
        // IsReadyForStreaming flag as the "haven't applied settings on this
        // open port yet" signal: cleared in Disconnect(), flipped true inside
        // WriteSettings(), so it auto-rearms on every fresh port open.
        if (_hub.IsReadyForStreaming) return;

        // Brand-new connection. Sanity-probe the firmware first so we have
        // a version logged regardless of whether the settings command
        // actually works - useful for distinguishing "device is silent"
        // from "device is responsive but doesn't implement FF DC 07/08".
        try
        {
            var fwVersion = _hub.GetFirmwareVersion();
            ServiceLog.Info(
                fwVersion is null
                    ? $"[cnvs-conn] firmware version probe returned no data (serial={_hub.Serial})"
                    : $"[cnvs-conn] firmware version {fwVersion} (serial={_hub.Serial})");
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[cnvs-conn] firmware version probe threw {ex.GetType().Name}: {ex.Message}");
        }

        // Apply the persisted firmware settings FIRST - before anything else
        // touches FF DC 05 (the lighting writer is gated on
        // hub.IsReadyForStreaming, which only flips true after WriteSettings
        // completes). The firmware appears to drop FF DC 07 if any FF DC 05
        // has been sent since USB connect, so we have to be the FIRST thing
        // on the wire.
        var s = _store.Load().Devices.Cnvs;
        try
        {
            if (_hub.WriteSettings(s.PlayAnimation, s.PlayWhenPCOff))
            {
                ServiceLog.Info(
                    $"[cnvs-conn] applied persisted settings on connect (serial={_hub.Serial}): boot={s.PlayAnimation} leds={s.PlayWhenPCOff}");
            }
            else
            {
                Console.Error.WriteLine($"[cnvs-conn] WriteSettings on connect returned false; will retry next tick");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cnvs-conn] WriteSettings on connect threw {ex.GetType().Name}: {ex.Message}");
        }

        // Tell the lighting provider its DeviceFrame topology changed so
        // RgbBridge rebuilds the engine's device list. Without this nudge the
        // lighting page won't show a freshly-plugged CNVS until the next bridge
        // tick (~3 s).
        try { _lighting?.OnHubStateUpdated(); }
        catch { /* subscriber failures shouldn't bubble */ }
    }
}
