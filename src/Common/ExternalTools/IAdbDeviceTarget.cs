using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Common.ExternalTools;

public interface IAdbDeviceTarget
{
    string Serial { get; }
    string Package { get; }
    Task<string> ShellAsync(string command, CancellationToken ct);
    /// <summary>True while an adb install is in progress; suppresses escalation reboots.</summary>
    bool InstallInProgress { get; set; }
}
