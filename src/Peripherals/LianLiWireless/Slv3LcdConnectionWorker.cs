using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Discovers the SL-LCD Wireless fan screens and maps each to its fan
/// position (GetPosIndex, CmdType 201). Independent of <see cref="Slv3Hub"/>:
/// the screens are wired USB devices, not part of the RF link. Position is
/// queried once per
/// serial and cached, since GetPosIndex is a wired hardware fact that does not
/// change while the same physical screen stays plugged into the same fan.
/// </summary>
public sealed class Slv3LcdConnectionWorker : BackgroundService
{
    private const int PollMs = 2000;

    private readonly Slv3LcdHub _hub;
    private readonly DeviceControlGate _gate;
    private readonly Dictionary<string, int> _knownPositions = new(StringComparer.OrdinalIgnoreCase);

    public Slv3LcdConnectionWorker(Slv3LcdHub hub, DeviceControlGate gate)
    {
        _hub = hub;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("lianli-wireless"))
                {
                    _knownPositions.Clear();
                    _hub.UpdateScreens(Array.Empty<Slv3LcdScreenInfo>());
                }
                else
                {
                    RefreshScreens();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[lianli-wireless-lcd] connection worker error: {ex.Message}");
            }

            await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
        }
    }

    private void RefreshScreens()
    {
        var ports = _hub.Discover();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var screens = new List<Slv3LcdScreenInfo>(ports.Count);

        foreach (var port in ports)
        {
            if (string.IsNullOrWhiteSpace(port.Serial))
            {
                continue;
            }
            seen.Add(port.Serial);

            if (!_knownPositions.TryGetValue(port.Serial, out var position))
            {
                position = _hub.TryGetPosition(port.Serial, out var groupIndex) ? groupIndex : -1;
                if (position >= 0)
                {
                    _knownPositions[port.Serial] = position;
                }
            }

            screens.Add(new Slv3LcdScreenInfo { Serial = port.Serial, Position = position });
        }

        foreach (var serial in new List<string>(_knownPositions.Keys))
        {
            if (!seen.Contains(serial))
            {
                _knownPositions.Remove(serial);
            }
        }

        _hub.UpdateScreens(screens);
    }
}
