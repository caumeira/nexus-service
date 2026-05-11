// qOS local service — Minimal API host for Native AOT.
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

// Dashboard "Launch" button targets qos://start-admin so the service
// comes up elevated. If we were started via that URL and we're not already
// elevated, spawn an elevated child (UAC prompt) and exit. Has to happen
// before the single-instance mutex so the elevated child can acquire it.
if (args.Length > 0
    && args[0].StartsWith("qos://start-admin", StringComparison.OrdinalIgnoreCase))
{
    if (OperatingSystem.IsWindows()
        && !Qos.Service.Platform.ProcessElevation.GetCurrent().IsElevated)
    {
        var elevateResult = Qos.Service.Lifecycle.ProcessRelauncher.TryRelaunchAsAdmin();
        if (elevateResult == Qos.Service.Lifecycle.RelaunchResult.Started)
        {
            return 0;
        }
        // UAC denied / failed: fall through and start unelevated. The
        // dashboard popover will still offer "Restart as administrator".
    }
}

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
// schtask: when the user enables "Start qOS on system startup", the
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
System.Threading.Mutex singleInstance;
bool isFirst;
{
    var deadline = DateTime.UtcNow.AddSeconds(isRelaunchElevated ? 10 : 0);
    while (true)
    {
        singleInstance = new System.Threading.Mutex(true, "Global\\QosServiceMutex", out isFirst);
        if (isFirst) break;
        singleInstance.Dispose();
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
// don't ambush the user with UAC at sign-in.
if (OperatingSystem.IsWindows()
    && !isRelaunchElevated
    && !suppressStartupWindow
    && !Qos.Service.Platform.ProcessElevation.GetCurrent().IsElevated)
{
    var coldStartElevation = Qos.Service.Lifecycle.ProcessRelauncher.TryRelaunchAsAdmin();
    if (coldStartElevation == Qos.Service.Lifecycle.RelaunchResult.Started)
    {
        return 0;
    }
    // UAC denied or relaunch failed: fall through and start unelevated.
}

// Strip qos:// protocol args - these come from browser launches.
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
// AddqOSLighting before AddqOSDevices uses it as ILightingDeviceProvider).
builder.Services
    .AddqOSCore()
    .AddqOSSensors()
    .AddqOSCooling()
    .AddqOSBenchmarks()
    .AddqOSLighting()
    .AddqOSDevices()
    .AddqOSPeripherals()
    .AddqOSActivity()
    .AddqOSNetwork()
    .AddqOSLifecycle()
    .AddqOSWeather()
    .AddqOSPanel(servicePort)
    .AddqOSLinuxDBus();

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

    var tokens = ctx.RequestServices.GetRequiredService<TokenService>();
    var panelPairing = ctx.RequestServices.GetRequiredService<Qos.Service.Panel.PanelPhonePairingService>();
    var requestToken = AuthRequestPolicy.ExtractBearerOrQueryToken(ctx);
    if (tokens.Validate(requestToken))
    {
        await next(ctx);
        return;
    }

    var cookieToken = ctx.Request.Cookies[Qos.Service.Panel.PanelPhonePairingService.SessionCookieName];
    var hasPanelSession =
        panelPairing.ValidateSessionToken(requestToken, ctx) ||
        (!string.Equals(requestToken, cookieToken, StringComparison.Ordinal) &&
         panelPairing.ValidateSessionToken(cookieToken, ctx));
    if (hasPanelSession)
    {
        if (AuthRequestPolicy.RejectsInsecureCsrf(ctx))
        {
            await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 403, "CSRF", "Cross-site request blocked.");
            return;
        }

        if (AuthRequestPolicy.IsPanelSessionAllowed(ctx))
        {
            await next(ctx);
            return;
        }

        await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 403, "Forbidden", "This action requires the desktop app.");
        return;
    }

    await Qos.Service.Auth.AuthErrorResponse.WriteAsync(ctx, 401, "Unauthorized", "This panel is not paired with the qOS service.");
});

// ── Map all routes ──
app.MapPingEndpoints();
app.MapAuthEndpoints();
app.MapSystemEndpoints();
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

// System tray icon (Windows only) — hides console, shows tray with right-click menu
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
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

// Cross-platform overlay host wiring (Windows qos-overlay.exe sidecar
// or Mac qos-overlay-helper Swift sidecar - same predicate either way).
// Reconciles on every settings change: run iff
// (OverlayWidgetsEnabled && layout.Count > 0); stop otherwise.
{
    var overlayHost = app.Services.GetRequiredService<Qos.Service.Panel.IOverlayHost>();
    var overlayStore = app.Services.GetRequiredService<IConfigStore>();
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
            else if (!shouldRun && overlayHost.IsRunning)
            {
                overlayHost.Stop();
            }
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
        if (!suppressStartupWindow)
        {
            Qos.Service.Platform.Windows.TrayIcon.OpenLocalWindow();
            Console.WriteLine("[qos-service] app window launched");
        }
        else
        {
            Console.WriteLine("[qos-service] startup window suppressed (--no-window)");
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
    // double-clicking qOS.app behaves like the Windows tray launch:
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
        // re-launches qOS.app while it's already running, or clicks the
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
