// Nexus local service — Minimal API host for Native AOT.
//
// Default bind: http://localhost:9400.
// Override with the first command-line arg:
//     nexus-service http://localhost:9400

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.DependencyInjection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Security;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

// Early-exit CLI flags (install/uninstall/tray/--open-app/protocol URLs,
// or no-args double-click on Windows). Each handler short-circuits the
// daemon startup. Runs before the single-instance mutex because
// --install-pawnio is briefly a second instance during the elevated install.
if (Nexus.Service.Lifecycle.CommandLineEntry.TryEarlyExit(args) is int earlyExit)
    return earlyExit;

// Pull out the lifecycle flags that gate behaviour later (SCM service
// mode, --no-window startup suppression, --relaunch-elevated self-elevation
// follow-up). The cleaned args are forwarded to the host below.
var (cliArgs, serviceMode, suppressStartupWindow, isRelaunchElevated) =
    Nexus.Service.Lifecycle.CommandLineEntry.StripLifecycleFlags(args);
args = cliArgs;

// Capture stdout / stderr to a rotating service.log file before anything else
// writes to the console. Doesn't change Console behaviour - just tees output.
Nexus.Service.Platform.ServiceLog.Initialize();

var url = ServiceLaunchIntent.ResolveServiceUrl(args);
var servicePort = ServiceLaunchIntent.ResolveServicePort(url);

// Single-instance guard — if another nexus-service is already running,
// open or focus the dashboard window instead of spawning a second service.
// When relaunching elevated, retry for up to 10s while the parent shuts down.
// Skipped under SCM: the service controller already enforces single-instance.
System.Threading.Mutex? singleInstance = null;
if (!serviceMode)
{
    bool isFirst;
    var deadline = DateTime.UtcNow.AddSeconds(isRelaunchElevated ? 10 : 0);
    while (true)
    {
        singleInstance = new System.Threading.Mutex(true, "Global\\NexusServiceMutex", out isFirst);
        if (isFirst) break;
        singleInstance.Dispose();
        singleInstance = null;
        if (!isRelaunchElevated || DateTime.UtcNow >= deadline)
        {
            Console.WriteLine("[nexus-service] already running, opening dashboard window");
            OpenExistingServiceWindow(servicePort);
            return 0;
        }
        System.Threading.Thread.Sleep(150);
    }
}
using var _singleInstance = singleInstance;

// Cold-start self-elevation: when the user double-clicks the EXE while no
// service is running and we're not yet elevated, prompt for UAC and let the
// elevated child take over the mutex. The manifest is asInvoker, so this is
// the only path that triggers a UAC prompt; subsequent double-clicks while
// the service is running hit the second-instance handoff above and never
// prompt. --no-window means schtask logon launch - skip auto-elevate so we
// don't ambush the user with UAC at sign-in. --service skips because SCM
// already runs us as LocalSystem.
if (OperatingSystem.IsWindows()
    && !isRelaunchElevated
    && !suppressStartupWindow
    && !serviceMode
    && !Nexus.Service.Platform.ProcessElevation.GetCurrent().IsElevated)
{
    var coldStartElevation = Nexus.Service.Lifecycle.ProcessRelauncher.TryRelaunchAsAdmin();
    if (coldStartElevation == Nexus.Service.Lifecycle.RelaunchResult.Started)
    {
        return 0;
    }
    // UAC denied or relaunch failed: fall through and start unelevated.
}

// Bind on all interfaces so phones on the same LAN can reach the panel
// phone pairing surface without internet.
var httpsPort = servicePort == 9400 ? 9443 : servicePort + 443;
X509Certificate2? localHttpsCertificate = null;
try
{
    localHttpsCertificate = LocalHttpsCertificate.LoadOrCreate();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[nexus-service] local HTTPS disabled: {ex.Message}");
}

// Set content root to the exe's directory so wwwroot/ is found
// regardless of which directory the user double-clicks from.
var exeDir = AppContext.BaseDirectory;
// Dev override: if `<CommonAppData>/Nexus/wwwroot-dev/index.html` exists,
// serve from there instead of the installed wwwroot. Lets us replace the
// SPA bundle on a running install without elevating into Program Files
// (which on the Q60 bench rig triggers a USB perturbation that degrades
// the device's WebView GPU state). The override is a sibling of the
// installer payload, not a merge — it fully shadows the bundled wwwroot
// when present, so the dev push must contain a full SPA build.
static string ResolveWebRoot(string exeDir)
{
    var defaultRoot = Path.Combine(exeDir, "wwwroot");
    if (!OperatingSystem.IsWindows()) return defaultRoot;
    try
    {
        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrEmpty(commonAppData)) return defaultRoot;
        var dev = Path.Combine(commonAppData, "Nexus", "wwwroot-dev");
        if (File.Exists(Path.Combine(dev, "index.html")))
        {
            Console.Error.WriteLine($"[nexus-service] wwwroot dev override active: {dev}");
            return dev;
        }
    }
    catch { /* fall through to default */ }
    return defaultRoot;
}
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir,
    WebRootPath = ResolveWebRoot(exeDir),
});
// Kestrel + form upload body limits. Default Kestrel cap is 30 MB which drops
// larger multipart uploads before /media/import sees them (the browser then
// reports "could not reach the service"). Match MediaImporter.MaxFileSize so
// the route-level check is the only place we reject oversized uploads.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = Nexus.Service.Media.MediaImporter.MaxFileSize;
    k.ListenAnyIP(servicePort);
    if (localHttpsCertificate is not null)
    {
        k.ListenAnyIP(httpsPort, o => o.UseHttps(localHttpsCertificate));
    }
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = Nexus.Service.Media.MediaImporter.MaxFileSize;
    o.ValueLengthLimit = int.MaxValue;
});

// JSON - source-generated for AOT (no reflection). Enums serialize as
// integers by default; opt specific enums into string form with a typed
// JsonStringEnumConverter<TEnum> on the type, never the non-generic global
// converter (not AOT-safe).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

// CORS - loopback for the bundled SPA, plus the public web app at
// nexusqos.com (HTTPS only). The hosted SPA fetches /pair to obtain a token,
// then talks to the local service on http://localhost:9400 from the browser.
// Token-based auth still gates every state-changing endpoint, so widening the
// origin list does not weaken the CSRF posture - the attacker would still need
// the per-installation token, which only loopback callers can request.
var allowedOrigins = BuildAllowedOrigins(servicePort, localHttpsCertificate is not null ? httpsPort : 0);
#if DEBUG
// Debug build: accept any http://localhost:* or http://127.0.0.1:* so the
// Vite dev server on a random port (5173-5180 etc.) can reach the service
// during frontend iteration. Loopback-only, no public surface exposed.
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin =>
        origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
    .AllowAnyMethod()
    .AllowAnyHeader()));
#else
// Release/AOT build: strict whitelist so production only accepts the SPA
// served from the service itself.
builder.Services.AddCors(c => c.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyMethod()
    .AllowAnyHeader()));
#endif

builder.Services.AddHttpClient();

// All DI registrations live in per-domain extension methods under
// src/DependencyInjection/. Order matters only where there are cross-domain
// dependencies (e.g. Lighting consumes the OpenRGB controller registered in
// AddQosLighting before AddQosDevices uses it as ILightingDeviceProvider).
builder.Services
    .AddQosCore()
    .AddQosSensors()
    .AddQosCooling()
    .AddQosBenchmarks()
    .AddQosLighting()
    .AddQosDevices()
    .AddQosPeripherals()
    .AddQosActivity()
    .AddQosNetwork()
    .AddQosLifecycle()
    .AddQosWeather()
    .AddQosWidgets()
    .AddQosPanel(servicePort)
    .AddQosLinuxDBus()
    .AddQosHelper();

// mDNS / Bonjour advertiser for the iOS companion app's Wi-Fi discovery.
// Reads HttpsPort + SpkiFingerprint + MachineName off PanelPhonePairingService
// after the Pairing config block below has populated them.
builder.Services.AddHostedService<Nexus.Service.Discovery.MdnsAdvertiser>();

// ── Build ──
var app = builder.Build();

Nexus.Service.Lifecycle.AppBootstrap.EagerInitGpu(app);
Nexus.Service.Lifecycle.AppBootstrap.InitializeProfiles(app);
Nexus.Service.Lifecycle.AppBootstrap.WireBeatsAndPresence(app);

// Middleware pipeline
var wsOptions = new WebSocketOptions();
#if !DEBUG
// Release/AOT: pin WS to the service's own origins.
foreach (var origin in allowedOrigins)
{
    wsOptions.AllowedOrigins.Add(origin);
}
// In Debug we leave AllowedOrigins empty so the WS handshake accepts any
// loopback origin (matches the loose HTTP CORS above). Token-based auth on
// the query string still gates authenticated endpoints.
#endif
app.UseWebSockets(wsOptions);

app.UseQosSecurityHeaders();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors();

app.UseQosPathAuth();

// ── Map all routes ──
app.MapPingEndpoints();
app.MapDefaultsEndpoints();
app.MapAuthEndpoints();
app.MapSystemEndpoints();
app.MapServiceControlEndpoints();
app.MapCoolingEndpoints();
app.MapBenchmarkEndpoints();
app.MapLightingEndpoints();
app.MapObsEndpoints();
app.MapSteamEndpoints();
app.MapDiscordEndpoints();
app.MapDevicesEndpoints();
app.MapPeripheralEndpoints();
app.MapKeebEndpoints();
app.MapDisplayEndpoints();
app.MapActivityEndpoints();
app.MapLifecycleEndpoints();
app.MapMediaLibraryEndpoints();
app.MapProfileEndpoints();
app.MapPanelEndpoints();
app.MapPanelMacroRoutes();
app.MapOverlayEndpoints();
app.MapWeatherEndpoints();
app.MapWidgetEndpoints();
app.MapConflictEndpoints();
app.MapWebSocketEndpoints();

{
    var pairing = app.Services.GetRequiredService<Nexus.Service.Panel.PanelPhonePairingService>();
    pairing.ServicePort = servicePort;
    pairing.HttpsPort = localHttpsCertificate is not null ? httpsPort : 0;
    pairing.SpkiFingerprint = localHttpsCertificate is not null
        ? Nexus.Service.Security.LocalHttpsCertificate.ComputeSpkiBase64Url(localHttpsCertificate)
        : string.Empty;
}

// SPA fallback
app.MapFallbackToFile("index.html");

// Kill orphan processes from previous crashed sessions.
Nexus.Service.Platform.FfmpegTracker.CleanupOrphans();
Nexus.Service.Panel.PanelOverlayHostLauncher.CleanupOrphans();
Nexus.Service.Lighting.Rgb.OpenRgbProcessManager.CleanupOrphans();

// Register nexus:// protocol handler (idempotent — safe on every launch)
Nexus.Service.Platform.ProtocolHandler.Register();

Console.WriteLine($"[nexus-service] listening on {url}");

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !serviceMode)
    Nexus.Service.Platform.Windows.TrayBootstrap.ConfigureTray(app);

#if WINDOWS
if (serviceMode)
    Nexus.Service.Platform.Windows.TrayBootstrap.WireHelperPipe(app);
#endif

Nexus.Service.Panel.OverlayHostBootstrap.Wire(app);

if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    Nexus.Service.Platform.Windows.TrayBootstrap.WireAppWindowAndPawnIo(app, serviceMode, suppressStartupWindow);

if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    return Nexus.Service.Platform.Mac.MacAppBootstrap.Run(app, servicePort);

#if WINDOWS
if (serviceMode)
{
    return Nexus.Service.Lifecycle.WindowsServiceHost.Run(args, async (_, ct) =>
    {
        await app.RunAsync(ct).ConfigureAwait(false);
        return 0;
    });
}
#endif

app.Run();
return 0;

static string[] BuildAllowedOrigins(int httpPort, int httpsPort)
{
    var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        $"http://localhost:{httpPort}",
        $"http://127.0.0.1:{httpPort}",
        "https://nexusqos.com",
        "https://www.nexusqos.com",
    };

    if (httpsPort > 0)
    {
        origins.Add($"https://localhost:{httpsPort}");
        origins.Add($"https://127.0.0.1:{httpsPort}");
    }

    if (!string.IsNullOrWhiteSpace(Environment.MachineName))
    {
        origins.Add($"http://{Environment.MachineName}:{httpPort}");
        if (httpsPort > 0)
            origins.Add($"https://{Environment.MachineName}:{httpsPort}");
    }

    try
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip) &&
                    !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    origins.Add($"http://{ip}:{httpPort}");
                    if (httpsPort > 0)
                        origins.Add($"https://{ip}:{httpsPort}");
                }
            }
        }
    }
    catch { }

    return origins.ToArray();
}

// ── Local functions ─────────────────────────────────────────────────────────

static void OpenExistingServiceWindow(int servicePort)
{
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Nexus.Service.Platform.Windows.TrayIcon.OpenLocalWindow(servicePort);
            return;
        }

        OpenInAppMode(ServiceLaunchIntent.LocalDashboardUrl(servicePort));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[nexus-service] second launch window handoff failed: {ex.Message}");
    }
}

static void OpenInAppMode(string url)
{
    // Find Chrome or Edge and launch with --app=URL for a chromeless window
    // (no address bar, no tabs) — similar to msedge.exe --app on Windows.
    // Fall back to the default browser if neither is installed.
    try
    {
        string? browser = null;
        string[] candidates = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new[]
            {
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
                "/Applications/Arc.app/Contents/MacOS/Arc",
                "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser",
            }
            : Array.Empty<string>();

        foreach (var path in candidates)
        {
            if (System.IO.File.Exists(path))
            { browser = path; break; }
        }

        if (browser is not null)
        {
            var dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-app");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = browser,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add($"--app={url}");
            psi.ArgumentList.Add($"--user-data-dir={dataDir}");
            System.Diagnostics.Process.Start(psi);
            return;
        }

        // Fallback: open in default browser
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[mac-status-bar] open {url} failed: {ex.Message}");
    }
}
