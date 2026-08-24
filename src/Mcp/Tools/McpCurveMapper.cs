using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Shared CurveDocument to wire Curve mapping for the cooling MCP tools,
/// mirroring CoolingRoutes' GET /cooling/curves shape so an AI client sees the
/// same curve data a dashboard request would.
/// </summary>
internal static class McpCurveMapper
{
    public static Curve ToWireCurve(CurveDocument d) => Nexus.Service.Cooling.CurveWireMapper.ToWire(d);
}
