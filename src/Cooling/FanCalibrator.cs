using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Sensors;
using LibreHardwareMonitor.Hardware;

namespace Nexus.Service.Cooling;

/// <summary>
/// Probes a fan's PWM→RPM response by ramping duty from 100% to 0% in 10%
/// steps, waiting for the RPM to stabilize at each level, then classifying
/// the fan based on the resulting curve. Pure algorithm - no DI, no routes,
/// no persistence. Called by the provider's CalibrateAsync method.
/// </summary>
public sealed class FanCalibrator
{
    private const int Steps = 11;
    private const int StepSize = 10;
    private const int SampleIntervalMs = 250;
    private const int MinSettleMs = 1500;
    private const int MaxWaitMs = 6000;
    private const int WindowSize = 6;

    private readonly LhmComputer _lhm;

    public FanCalibrator(LhmComputer lhm) => _lhm = lhm;

    public async Task<FanCalibration> CalibrateOneAsync(
        string fanId,
        ISensor controlSensor,
        ISensor fanSensor,
        IProgress<FanCalibrationProgress>? progress,
        CancellationToken ct)
    {
        var curve = new List<FanCalibrationPoint>(Steps);

        try
        {
            for (int i = 0; i < Steps; i++)
            {
                ct.ThrowIfCancellationRequested();

                var duty = 100 - i * StepSize;
                controlSensor.Control?.SetSoftware(duty);

                progress?.Report(new FanCalibrationProgress
                {
                    FanId = fanId,
                    CurrentDuty = duty,
                    StepIndex = i,
                    TotalSteps = Steps,
                    State = "Settling",
                });

                var rpm = await MeasureStableRpmAsync(fanSensor, ct);
                curve.Add(new FanCalibrationPoint { Duty = duty, Rpm = rpm });

                progress?.Report(new FanCalibrationProgress
                {
                    FanId = fanId,
                    CurrentDuty = duty,
                    CurrentRpm = rpm,
                    StepIndex = i,
                    TotalSteps = Steps,
                    State = "Recording",
                });
            }
        }
        finally
        {
            controlSensor.Control?.SetDefault();
        }

        var result = Classify(fanId, curve);

        progress?.Report(new FanCalibrationProgress
        {
            FanId = fanId,
            CurrentRpm = result.MaxRpm,
            StepIndex = Steps - 1,
            TotalSteps = Steps,
            State = "Done",
        });

        return result;
    }

    private async Task<int> MeasureStableRpmAsync(ISensor fanSensor, CancellationToken ct)
    {
        var samples = new Queue<int>();
        var elapsed = 0;

        while (elapsed < MaxWaitMs)
        {
            await Task.Delay(SampleIntervalMs, ct);
            _lhm.Update(TimeSpan.FromMilliseconds(100));
            var rpm = (int)(fanSensor.Value ?? 0f);
            samples.Enqueue(rpm);
            if (samples.Count > WindowSize) samples.Dequeue();
            elapsed += SampleIntervalMs;

            if (elapsed >= MinSettleMs && samples.Count == WindowSize && IsStable(samples))
                break;
        }

        return samples.Count > 0 ? (int)samples.Average() : 0;
    }

    private static bool IsStable(IEnumerable<int> samples) => FanCalibrationLogic.IsStable(samples);
    private static FanCalibration Classify(string fanId, List<FanCalibrationPoint> curve) => FanCalibrationLogic.Classify(fanId, curve);
}
