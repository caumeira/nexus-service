using System.Collections.Generic;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Platform;

public sealed class DefaultMonitorEnumerator : IMonitorEnumerator
{
    public List<ScreenSyncMonitor> Enumerate() => MonitorEnumerator.List();
}
