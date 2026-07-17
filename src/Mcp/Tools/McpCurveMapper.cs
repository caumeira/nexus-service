using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using GraphPoint = Nexus.Service.Models.Cooling.GraphPoint;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Shared CurveDocument to wire Curve mapping for the cooling MCP tools,
/// mirroring CoolingRoutes' GET /cooling/curves shape so an AI client sees the
/// same curve data a dashboard request would.
/// </summary>
internal static class McpCurveMapper
{
    public static Curve ToWireCurve(CurveDocument d) => new()
    {
        Id = d.Id,
        Name = d.Name,
        Type = d.Type,
        Input = new CurveInput { Id = d.Input.Id, Type = d.Input.Type, Device = d.Input.Device },
        Outputs = d.Outputs.Select(o => new CurveOutput { Id = o.Id, Type = o.Type }).ToList(),
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
            Points = d.Graph.Points.Select(p => new GraphPoint { Temp = p.Temp, Speed = p.Speed }).ToList(),
        },
        Mixed = d.Mixed is null ? null : new MixedCurve
        {
            ResponseTime = d.Mixed.ResponseTime,
            CurveIds = new List<string>(d.Mixed.CurveIds),
            Fn = d.Mixed.Fn,
        },
        Preset = d.Preset,
    };
}
