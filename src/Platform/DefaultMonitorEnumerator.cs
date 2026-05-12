using System.Collections.Generic;
using Qos.Service.Models.Lighting;

namespace Qos.Service.Platform;

public sealed class DefaultMonitorEnumerator : IMonitorEnumerator
{
    public List<ScreenSyncMonitor> Enumerate() => MonitorEnumerator.List();
}
