using System.Runtime.InteropServices;
using Qos.Service.Models;
using Qos.Service.Panel;

namespace Qos.Service.Routes;

public static class PingRoutes
{
    public static void MapPingEndpoints(this WebApplication app)
    {
        var platform = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : "linux";

        // MachineName resolves through PanelPhonePairingService so a host-name
        // override set via /panel/host-name is reflected immediately on the
        // next ping (no service restart).
        app.MapGet("/ping", (PanelPhonePairingService pairing) => new PingResponse
        {
            Service = "qos-service",
            Version = BuildInfo.Version,
            Initialized = true,
            Platform = platform,
            MachineName = pairing.MachineName,
        });
    }
}
