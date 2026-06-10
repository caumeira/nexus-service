using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Sockets;

namespace Nexus.Service.Cooling;

public enum CalibrationState { Idle, Running, Complete }

/// <summary>
/// Singleton that manages background fan calibration. POST /cooling/calibrate
/// starts calibration via <see cref="Start"/> which returns immediately. The
/// calibration runs in a background task. Poll <see cref="State"/> and
/// <see cref="Results"/> to track progress and completion. Start and
/// finish push the cooling topic so clients flip their
/// calibration-in-progress UI immediately instead of waiting for an unrelated
/// cooling broadcast to happen by.
/// </summary>
public sealed class CalibrationRunner
{
    private readonly MultiplexHub _hub;
    private readonly object _lock = new();
    private Task? _task;

    public CalibrationRunner(MultiplexHub hub)
    {
        _hub = hub;
    }

    public CalibrationState State { get; private set; } = CalibrationState.Idle;
    public List<FanCalibrationProgress> Progress { get; } = new();
    public IReadOnlyList<FanCalibration> Results { get; private set; } = Array.Empty<FanCalibration>();

    public bool Start(IFanControlProvider provider, IReadOnlyList<string> fanIds)
    {
        lock (_lock)
        {
            if (State == CalibrationState.Running)
            {
                return false;
            }

            State = CalibrationState.Running;
            Progress.Clear();
            Results = Array.Empty<FanCalibration>();

            var progress = new Progress<FanCalibrationProgress>(p =>
            {
                lock (_lock)
                {
                    Progress.Add(p);
                }
            });

            _task = Task.Run(async () =>
            {
                try
                {
                    var results = await provider.CalibrateAsync(fanIds, progress, CancellationToken.None);
                    lock (_lock)
                    {
                        Results = results;
                        State = CalibrationState.Complete;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[calibration] failed: {ex.Message}");
                    lock (_lock)
                    {
                        State = CalibrationState.Idle;
                    }
                }
                PanelTopics.BroadcastCooling(_hub);
            });
        }
        PanelTopics.BroadcastCooling(_hub);
        return true;
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (State == CalibrationState.Complete)
            {
                State = CalibrationState.Idle;
            }
        }
    }
}
