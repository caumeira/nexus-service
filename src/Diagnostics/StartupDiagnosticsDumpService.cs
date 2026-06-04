using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Writes a one-time hardware/specs snapshot to the service log shortly after
/// startup, so a tester's service.log opens with the full picture of what Nexus
/// detected — system specs plus every device, monitor, RGB and cooling
/// component as read at boot. Diagnostics only; never on the boot critical path.
///
/// Gated on <see cref="SystemSpecsCollector.GetAsync"/>, which awaits the sensor
/// provider's readiness, so the snapshot reflects enumerated hardware rather
/// than a half-initialised cold-boot state. Devices that connect later still
/// log their own connect lines, so nothing is lost.
/// </summary>
public sealed class StartupDiagnosticsDumpService : BackgroundService
{
    private const string Tag = "[startup-diagnostics]";

    private readonly SystemSpecsCollector _specs;
    private readonly DeviceManager _devices;
    private readonly ILightingDeviceProvider _lighting;
    private readonly ICoolingProvider _cooling;
    private readonly IMonitorEnumerator _monitors;

    public StartupDiagnosticsDumpService(
        SystemSpecsCollector specs,
        DeviceManager devices,
        ILightingDeviceProvider lighting,
        ICoolingProvider cooling,
        IMonitorEnumerator monitors)
    {
        _specs = specs;
        _devices = devices;
        _lighting = lighting;
        _cooling = cooling;
        _monitors = monitors;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hand control back to StartAsync immediately; the dump gathers on a
        // thread-pool thread so /ping stays reachable during boot.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;

        SystemSpecsResponse? specs = null;
        try
        {
            // Awaits the sensor provider's readiness, so the snapshot runs once
            // hardware enumeration has settled rather than mid-probe. Bounded: a
            // stalled provider must not suppress the whole snapshot — emit the rest
            // with a note rather than silently never logging anything.
            using var specsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            specsCts.CancelAfter(TimeSpan.FromSeconds(30));
            specs = await _specs.GetAsync(specsCts.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (OperationCanceledException) { Emit("system specs unavailable: sensor provider not ready within 30s"); }
        catch (Exception ex) { Emit($"system specs read failed: {ex.Message}"); }

        Emit("===== Nexus startup snapshot =====");

        if (specs is not null)
        {
            Emit($"host: {specs.PcName} | {specs.OsBuild}");
            Emit($"cpu: {specs.Processor}");
            Emit($"gpu: {specs.GraphicsCard}");
            Emit($"board: {specs.Motherboard}");
            Emit($"memory: {specs.Memory}");
            Emit($"storage: {specs.Storage}");
            Emit($"display: {specs.Monitor}");
            Emit($"audio: {specs.SoundCard}");
            Emit($"network: {specs.NetworkCard}");
        }

        try
        {
            var devices = _devices.GetAll();
            Emit($"devices ({devices.Count}):");
            foreach (var d in devices)
            {
                var fw = string.IsNullOrEmpty(d.FirmwareVersion) ? "-" : d.FirmwareVersion;
                Emit($"  - [{d.Category}] {d.Name} connected={d.Connected} fw={fw} type={d.FirmwareType} id={d.Id}");
            }
        }
        catch (Exception ex) { Emit($"devices read failed: {ex.Message}"); }

        try
        {
            var monitors = _monitors.Enumerate();
            Emit($"monitors ({monitors.Count}):");
            foreach (var m in monitors)
                Emit($"  - {m.Name} id={m.Id}");
        }
        catch (Exception ex) { Emit($"monitors read failed: {ex.Message}"); }

        try
        {
            var rgb = _lighting.GetAll();
            var devices = rgb.Devices ?? new System.Collections.Generic.List<Nexus.Service.Models.Devices.LightingDevice>();
            Emit($"lighting (init={rgb.IsInit}, {devices.Count} device(s)):");
            foreach (var l in devices)
                Emit($"  - [{l.Type}] {l.Name} leds={l.LedCount} id={l.Id}");
        }
        catch (Exception ex) { Emit($"lighting read failed: {ex.Message}"); }

        try
        {
            var cooling = _cooling.GetAll();
            Emit($"cooling ({cooling.Count} component(s)):");
            foreach (var c in cooling)
                Emit($"  - [{c.Type}] {c.Name} channels={c.Devices.Count} id={c.Id}");
        }
        catch (Exception ex) { Emit($"cooling read failed: {ex.Message}"); }

        Emit("===== end snapshot =====");
    }

    private static void Emit(string line) => ServiceLog.Info($"{Tag} {line}");
}
