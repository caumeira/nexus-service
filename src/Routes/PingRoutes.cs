using System.Runtime.InteropServices;
using Nexus.Service.Models;
using Nexus.Service.Panel;

namespace Nexus.Service.Routes;

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
            Service = "nexus-service",
            Version = BuildInfo.Version,
            Initialized = true,
            Platform = platform,
            MachineName = pairing.MachineName,
        });

        // Bare-bones health and config endpoints for the HYTE OEM Q-series
        // launcher (`com.companyname.thiccapp`). Its RN bootstrap polls
        // `/ready` and `/hardware/profile` on `ports.backend`; if either
        // returns non-2xx the launcher flips its WebView back to the idle
        // face. By serving stubs here on the nexus service we let the OEM's
        // WebView stay open and load our panel at `/panel/{deviceId}`.
        // Raw-JSON literals because the .NET 10 AOT JsonSerializer rejects
        // anonymous types at runtime - we'd otherwise hit 500s here.
        app.MapGet("/ready", () => Results.Content("{\"ready\":true}", "application/json"));
        app.MapGet("/hardware/profile", () => Results.Content(
            "{\"offlineView\":\"default\",\"disableDisplayWithoutSata\":false,\"intervalDuration\":0,\"mediaFiles\":[],\"text\":\"\",\"preview\":false}",
            "application/json"));
    }
}
