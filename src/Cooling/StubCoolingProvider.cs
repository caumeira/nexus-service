using System;
using System.Collections.Generic;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Cooling;

/// <summary>
/// Cross-platform stub. Reports no cooling hardware so the SPA renders an empty
/// "no cooling hardware detected" state instead of erroring. Curve registrations
/// persist to settings.json so the SPA can round-trip configs even before real
/// hardware control exists.
/// </summary>
public sealed class StubCoolingProvider : ICoolingProvider, ICurveProvider, IFanControlProvider
{
    private readonly IConfigStore _store;

    public StubCoolingProvider(IConfigStore store) { _store = store; }

    // ----- ICoolingProvider -----
    public IReadOnlyList<CoolingComponent> GetAll() => Array.Empty<CoolingComponent>();

    // ----- ICurveProvider -----
    public void SetCurves(SetCurvesBody body)
    {
        _store.Update(s =>
        {
            s.Cooling.GlobalSpeedModifier = body.GlobalSpeedModifier;
            s.Cooling.Curves.Clear();
            foreach (var c in body.Curves)
            {
                s.Cooling.Curves.Add(new CurveDocument
                {
                    Id = c.Id,
                    Name = c.Name,
                    Type = c.Type,
                    Input = new CurveInputDocument { Id = c.Input.Id, Type = c.Input.Type, Device = c.Input.Device },
                    Outputs = c.Outputs.ConvertAll(o => new CurveOutputDocument { Id = o.Id, Type = o.Type }),
                    Flat = c.Flat is null ? null : new FlatCurveData { Speed = c.Flat.Speed },
                    Linear = c.Linear is null ? null : new LinearCurveData
                    {
                        ResponseTime = c.Linear.ResponseTime,
                        MinTemp = c.Linear.MinTemp,
                        MaxTemp = c.Linear.MaxTemp,
                        MinSpeed = c.Linear.MinSpeed,
                        MaxSpeed = c.Linear.MaxSpeed,
                    },
                    Graph = c.Graph is null ? null : new GraphCurveData
                    {
                        ResponseTime = c.Graph.ResponseTime,
                        SpeedModifier = c.Graph.SpeedModifier,
                        Points = c.Graph.Points.ConvertAll(p => new Persistence.GraphPoint { Temp = p.Temp, Speed = p.Speed }),
                    },
                    Mixed = c.Mixed is null ? null : new Persistence.MixedCurveData
                    {
                        ResponseTime = c.Mixed.ResponseTime,
                        CurveIds = new List<string>(c.Mixed.CurveIds),
                        Fn = c.Mixed.Fn,
                    },
                    Preset = string.IsNullOrEmpty(c.Preset) ? null : c.Preset,
                });
            }
        });
    }

    public object? GetCalculatedById(string id) => null;

    // ----- IFanControlProvider -----
    public IReadOnlyList<FanChannel> GetFanChannels() => Array.Empty<FanChannel>();
    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();
    public float? ReadTemperature(string sensorId) => null;
    public int SetFanSpeed(string channelId, int dutyPercent) => System.Math.Clamp(dutyPercent, 0, 100);
    public void DriveFanSpeed(string channelId, int dutyPercent) { }
    public void ReleaseFan(string channelId) { }
    public void ReleaseAll() { }
    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
}
