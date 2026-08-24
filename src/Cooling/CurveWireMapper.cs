using System.Collections.Generic;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using WireGraphPoint = Nexus.Service.Models.Cooling.GraphPoint;
using DocGraphPoint = Nexus.Service.Persistence.GraphPoint;

namespace Nexus.Service.Cooling;

/// <summary>
/// Single mapping between the persisted <see cref="CurveDocument"/> and the wire
/// <see cref="Curve"/>. GET /cooling/curves, the MCP curve tools and the save
/// path all route through here, so a new curve type is added in one place.
/// </summary>
internal static class CurveWireMapper
{
    public static Curve ToWire(CurveDocument d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Type = d.Type,
        Input = new CurveInput { Id = d.Input.Id, Type = d.Input.Type, Device = d.Input.Device },
        Outputs = d.Outputs.ConvertAll(o => new CurveOutput { Id = o.Id, Type = o.Type }),
        Flat = d.Flat is null ? null : new FlatCurve { Speed = d.Flat.Speed },
        Linear = d.Linear is null ? null : new LinearCurve
        {
            ResponseTime = d.Linear.ResponseTime,
            MinTemp = d.Linear.MinTemp,
            MaxTemp = d.Linear.MaxTemp,
            MinSpeed = d.Linear.MinSpeed,
            MaxSpeed = d.Linear.MaxSpeed,
        },
        Graph = d.Graph is null ? null : new GraphCurve
        {
            ResponseTime = d.Graph.ResponseTime,
            SpeedModifier = d.Graph.SpeedModifier,
            Points = d.Graph.Points.ConvertAll(p => new WireGraphPoint { Temp = p.Temp, Speed = p.Speed }),
        },
        Mixed = d.Mixed is null ? null : new MixedCurve
        {
            ResponseTime = d.Mixed.ResponseTime,
            CurveIds = new List<string>(d.Mixed.CurveIds),
            Fn = d.Mixed.Fn,
        },
        Trigger = d.Trigger is null ? null : new TriggerCurve
        {
            ResponseTime = d.Trigger.ResponseTime,
            IdleTemp = d.Trigger.IdleTemp,
            LoadTemp = d.Trigger.LoadTemp,
            IdleSpeed = d.Trigger.IdleSpeed,
            LoadSpeed = d.Trigger.LoadSpeed,
        },
        Sync = d.Sync is null ? null : new SyncCurve
        {
            SourceChannelId = d.Sync.SourceChannelId,
            Offset = d.Sync.Offset,
            Proportional = d.Sync.Proportional,
        },
        Auto = d.Auto is null ? null : new AutoCurve
        {
            ResponseTime = d.Auto.ResponseTime,
            IdleTemp = d.Auto.IdleTemp,
            LoadTemp = d.Auto.LoadTemp,
            MinSpeed = d.Auto.MinSpeed,
            MaxSpeed = d.Auto.MaxSpeed,
            Step = d.Auto.Step,
            Deadband = d.Auto.Deadband,
        },
        Preset = d.Preset,
    };

    public static CurveDocument ToDocument(Curve c) => new()
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
            Points = c.Graph.Points.ConvertAll(p => new DocGraphPoint { Temp = p.Temp, Speed = p.Speed }),
        },
        Mixed = c.Mixed is null ? null : new MixedCurveData
        {
            ResponseTime = c.Mixed.ResponseTime,
            CurveIds = new List<string>(c.Mixed.CurveIds),
            Fn = c.Mixed.Fn,
        },
        Trigger = c.Trigger is null ? null : new TriggerCurveData
        {
            ResponseTime = c.Trigger.ResponseTime,
            IdleTemp = c.Trigger.IdleTemp,
            LoadTemp = c.Trigger.LoadTemp,
            IdleSpeed = c.Trigger.IdleSpeed,
            LoadSpeed = c.Trigger.LoadSpeed,
        },
        Sync = c.Sync is null ? null : new SyncCurveData
        {
            SourceChannelId = c.Sync.SourceChannelId,
            Offset = c.Sync.Offset,
            Proportional = c.Sync.Proportional,
        },
        Auto = c.Auto is null ? null : new AutoCurveData
        {
            ResponseTime = c.Auto.ResponseTime,
            IdleTemp = c.Auto.IdleTemp,
            LoadTemp = c.Auto.LoadTemp,
            MinSpeed = c.Auto.MinSpeed,
            MaxSpeed = c.Auto.MaxSpeed,
            Step = c.Auto.Step,
            Deadband = c.Auto.Deadband,
        },
        Preset = string.IsNullOrEmpty(c.Preset) ? null : c.Preset,
    };
}
