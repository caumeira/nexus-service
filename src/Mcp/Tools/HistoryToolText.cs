namespace Nexus.Service.Mcp.Tools;

/// <summary>isError text query_events returns when IAiEventLog.IsAvailable is
/// false. query_sensor_history/get_history_summary read the monitoring store
/// instead, which has no comparable unavailable state.</summary>
internal static class HistoryToolText
{
    internal const string Unavailable = "History storage is unavailable on this machine.";
}
