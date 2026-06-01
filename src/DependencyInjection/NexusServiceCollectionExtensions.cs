using System.Runtime.InteropServices;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Discord;
using Nexus.Service.Fps;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Obs;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Peripherals.QSeries;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using Nexus.Service.Sockets;
using Nexus.Service.Steam;
using Microsoft.Extensions.DependencyInjection;

namespace Nexus.Service.DependencyInjection;

/// <summary>
/// Per-domain DI extension methods. Program.cs composes them into a single
/// fluent chain instead of inlining ~400 lines of platform-conditional
/// AddSingleton blocks. Each method keeps its own #if WINDOWS / runtime
/// platform checks so the AOT trim model is unchanged.
/// </summary>
public static class NexusServiceCollectionExtensions
{
    public static IServiceCollection AddNexusCore(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<LhmComputer>();
        services.AddSingleton<IPerformanceProvider, Nexus.Service.Platform.Windows.WindowsPerformanceProvider>();
#else
        services.AddSingleton<IPerformanceProvider>(_ => PerformanceProviderFactory.Create());
#endif
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<TokenService>();

#if WINDOWS
        services.AddSingleton<IFpsProvider, WindowsFpsProvider>();
#else
        services.AddSingleton<IFpsProvider, StubFpsProvider>();
#endif
        services.AddSingleton<MultiplexHub>();
        services.AddSingleton<LightingOutputHub>();
        services.AddSingleton<Nexus.Service.Monitoring.MonitoringBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Monitoring.MonitoringBroadcaster>());
        // ConflictWatcher polls the running process list against
        // ConflictAppCatalog and publishes to the "conflicts" multiplex
        // topic. OpenRgbProcessManager is only registered on Win/Mac, so
        // we resolve it as optional so the watcher can ignore the bundled
        // child OpenRGB process where present.
        services.AddSingleton<Nexus.Service.Conflicts.ConflictWatcher>(sp =>
            new Nexus.Service.Conflicts.ConflictWatcher(
                sp.GetRequiredService<MultiplexHub>(),
                sp.GetService<Nexus.Service.Lighting.Rgb.OpenRgbProcessManager>()));
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Conflicts.ConflictWatcher>());
        return services;
    }

    public static IServiceCollection AddNexusSensors(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<ISensorProvider, LibreHardwareSensorProvider>();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            services.AddSingleton<ISensorProvider, LinuxSensorProvider>();
        else
            services.AddSingleton<ISensorProvider, MacSensorProvider>();
#endif
        services.AddSingleton<ProcessMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<ProcessMonitor>());
        services.AddSingleton<SystemSpecsCollector>();
        // Pre-warms the specs cache in the background after host start so the
        // first Devices → System Specs request doesn't pay a cold PowerShell
        // spawn. Hard rule: this MUST stay off the startup critical path —
        // see SystemSpecsPrewarmService.ExecuteAsync.
        services.AddHostedService<SystemSpecsPrewarmService>();
#if WINDOWS
        // Triggers the IFanControlProvider singleton ctor (which transitively
        // constructs LhmComputer + kicks off its background Open()) right
        // after host start. Same "warmup off critical path" pattern as
        // SystemSpecsPrewarmService. Windows-only because LhmComputer is the
        // only IFanControlProvider implementation that needs prewarming.
        services.AddHostedService<LhmWarmupService>();
#endif
        return services;
    }

    public static IServiceCollection AddNexusCooling(this IServiceCollection services)
    {
        services.AddSingleton<StubCoolingProvider>();
        // Pick the motherboard-side provider per platform, registered under
        // the concrete type. The public IFanControlProvider / ICoolingProvider
        // bindings below resolve to CompositeFanControlProvider so the curve
        // engine and routes see motherboard + NP50 channels through one shape.
#if WINDOWS
        services.AddSingleton<WindowsFanControlProvider>();
        services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
            sp.GetRequiredService<WindowsFanControlProvider>(),
            sp.GetRequiredService<Np50CoolingProvider>(),
            sp.GetRequiredService<MiniHubCoolingProvider>()));
        services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<MacFanControlProvider>();
            services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
                sp.GetRequiredService<MacFanControlProvider>(),
                sp.GetRequiredService<Np50CoolingProvider>(),
                sp.GetRequiredService<MiniHubCoolingProvider>()));
            services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
        }
        else if (OperatingSystem.IsLinux())
        {
            // hwmon (motherboard + AMD GPU via amdgpu) + liquidctl USB coolers
            // + NVIDIA GPU fans, layered onto the same composite the routes see.
            services.AddSingleton<LinuxFanControlProvider>();
            services.AddSingleton<LinuxLiquidctlProvider>();
            services.AddSingleton<LinuxNvidiaFanProvider>();
            services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
                sp.GetRequiredService<LinuxFanControlProvider>(),
                sp.GetRequiredService<Np50CoolingProvider>(),
                sp.GetRequiredService<MiniHubCoolingProvider>(),
                new CompositeFanControlProvider.FanSource(
                    LinuxLiquidctlProvider.IsLiquidctlId, sp.GetRequiredService<LinuxLiquidctlProvider>()),
                new CompositeFanControlProvider.FanSource(
                    LinuxNvidiaFanProvider.IsNvidiaId, sp.GetRequiredService<LinuxNvidiaFanProvider>())));
            services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
        }
        else
        {
            services.AddSingleton<IFanControlProvider>(sp => new CompositeFanControlProvider(
                sp.GetRequiredService<StubCoolingProvider>(),
                sp.GetRequiredService<Np50CoolingProvider>(),
                sp.GetRequiredService<MiniHubCoolingProvider>()));
            services.AddSingleton<ICoolingProvider>(sp => (ICoolingProvider)sp.GetRequiredService<IFanControlProvider>());
        }
#endif
        services.AddSingleton<Np50CoolingProvider>();
        services.AddSingleton<MiniHubCoolingProvider>();
        services.AddSingleton<ICurveProvider>(sp => sp.GetRequiredService<StubCoolingProvider>());
        services.AddSingleton<CurveEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<CurveEngine>());
        services.AddSingleton<CalibrationRunner>();
        return services;
    }

    public static IServiceCollection AddNexusLighting(this IServiceCollection services)
    {
        services.AddSingleton<LightingEngine>();
        services.AddSingleton(_ => new Nexus.Service.Lighting.Engine.Gpu.GpuContext(160, 90));
        services.AddSingleton<ILightingProvider, LightingProvider>();
        services.AddSingleton<IObsProvider, ObsProvider>();
        services.AddSingleton<ISteamProvider, SteamProvider>();
        services.AddSingleton<IDiscordProvider, DiscordProvider>();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Lighting.Rgb.OpenRgbProcessManager>();
            services.AddSingleton<Nexus.Service.Lighting.Rgb.IRgbController>(_ =>
                new Nexus.Service.Lighting.Rgb.OpenRgbController());
            services.AddSingleton<Nexus.Service.Lighting.Rgb.RgbBridge>();
        }
        else
        {
            services.AddSingleton<Nexus.Service.Lighting.Rgb.IRgbController, Nexus.Service.Lighting.Rgb.NoOpRgbController>();
        }

        if (OperatingSystem.IsWindows())
            services.AddHostedService<Nexus.Service.Lighting.Rgb.PowerEventListener>();

        return services;
    }

    public static IServiceCollection AddNexusDevices(this IServiceCollection services)
    {
        // CNVS hub: serial-port discovery + hub singleton + connection
        // worker that grabs COM7 at startup before OpenRGB-headless can
        // claim it (whoever opens the COM port first wins on Windows
        // serial — same race-and-hold pattern that lets NP50 and MiniHub
        // coexist with OpenRGB). Windows-only discovery; non-Windows gets
        // a stub that never finds anything.
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.ICnvsPortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Cnvs.WindowsCnvsPortDiscovery>();
#else
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.ICnvsPortDiscovery,
                                  Nexus.Service.Peripherals.Hyte.Cnvs.LinuxCnvsPortDiscovery>();
        }
        else
        {
            services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.ICnvsPortDiscovery,
                                  Nexus.Service.Peripherals.Hyte.Cnvs.StubCnvsPortDiscovery>();
        }
#endif
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Cnvs.CnvsHub>();
        services.AddHostedService<Nexus.Service.Peripherals.Hyte.Cnvs.CnvsConnectionWorker>();

        services.AddSingleton<StubDeviceProvider>();
        services.AddSingleton<IDeviceProvider>(sp => sp.GetRequiredService<StubDeviceProvider>());

        // Lighting provider composition: OpenRGB (motherboard / RAM / AIO /
        // etc.) + NP50 hub (LS10 / LS30 / FP12 daisy-chained off Nexus Link
        // ports). The composite routes by id prefix so existing
        // /devices/lighting-devices/* routes don't change shape.
        // Np50LightingDeviceProvider doubles as an ILightingFrameContributor
        // so the RgbBridge can include NP50 zones in the engine's DeviceFrame
        // array and the engine's per-tick OnFrame fires for them. The
        // Np50LightingFrameWriter hosted service consumes those frames and
        // pushes per-port LED buffers to the hub.
        services.AddSingleton<Nexus.Service.Lighting.Np50IdentifyTracker>();
        services.AddSingleton<Nexus.Service.Lighting.Np50LightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.Np50LightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingFrameWriter>());

        // MiniHub: lighting-only v1, mirrors the NP50 stack with a separate
        // hub coordinator + heartbeat + frame writer. Composite lighting
        // provider routes between OpenRGB / NP50 / MiniHub / CNVS by id prefix.
        services.AddSingleton<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.MiniHubLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingFrameWriter>());

        // CNVS lighting: our CnvsHub owns COM7, so OpenRGB no longer drives
        // the mat. This provider surfaces the 50-LED zone to the lighting
        // engine and the writer pushes 30 Hz LED frames to the hub. Reuses
        // the Np50IdentifyTracker (it's the shared flash-on-identify state).
        services.AddSingleton<Nexus.Service.Lighting.CnvsLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.CnvsLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingFrameWriter>());

        // Q-series cooler lighting: QSeriesCoolerHub owns the cooler's serial port
        // (OpenRGB no longer drives 1st-party HYTE devices), so this provider surfaces
        // the cooler LEDs and the writer pushes 30 Hz frames. Reuses Np50IdentifyTracker.
        services.AddSingleton<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.QSeriesLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingFrameWriter>());

        // Keeb TKL lighting: KeebHub owns the keyboard's vendor HID interface
        // (OpenRGB's "HYTE Keeb TKL" detector is disabled), this provider
        // surfaces the keys + underglow zones, and the writer streams 30 Hz
        // RGB frames directly over HID. Reuses Np50IdentifyTracker for the
        // shared identify-flash state.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Keeb.KeebHub>();
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Keeb.KeebSettingsApplier>();
        services.AddSingleton<Nexus.Service.Lighting.KeebLightingDeviceProvider>();
        services.AddSingleton<Nexus.Service.Lighting.ILightingFrameContributor>(
            sp => sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>());
        services.AddSingleton<Nexus.Service.Lighting.KeebLightingFrameWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingFrameWriter>());
        services.AddHostedService(sp => new Nexus.Service.Peripherals.Hyte.Keeb.KeebConnectionWorker(
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Keeb.KeebHub>(),
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Keeb.KeebSettingsApplier>(),
            sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>()));
        services.AddHostedService<Nexus.Service.Peripherals.Hyte.Keeb.KeebInputWorker>();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider>(sp => new Nexus.Service.Lighting.CompositeLightingDeviceProvider(
                sp.GetRequiredService<Nexus.Service.Lighting.Rgb.OpenRgbLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>()));
        }
        else
        {
            services.AddSingleton<ILightingDeviceProvider>(sp => new Nexus.Service.Lighting.CompositeLightingDeviceProvider(
                sp.GetRequiredService<StubDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Np50LightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.MiniHubLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.CnvsLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.QSeriesLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Lighting.KeebLightingDeviceProvider>(),
                sp.GetRequiredService<Nexus.Service.Persistence.IConfigStore>(),
                sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>()));
        }

        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.CnvsHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.QSeriesHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Y70Handler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.KeebHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.FanHubHandler>();
        services.AddSingleton<IDeviceHandler, Nexus.Service.Devices.Handlers.Np50Handler>();

        // Read-only catalog of firmware images embedded in this build. Backs
        // the Firmware Updates page's "available version" column.
        services.AddSingleton<Nexus.Service.Devices.Firmware.BundledFirmwareCatalog>();

        // Firmware flasher: dfu-util wrapper + WinUSB installer + orchestrator.
        // CnvsHub doubles as a DFU flash target (it owns the CNVS serial port and
        // can drop the device into the bootloader). Other hubs join IDfuFlashTarget
        // as their EnterDfuMode lands.
        services.AddSingleton<Nexus.Service.Devices.Firmware.DfuUtil>(_ =>
            new Nexus.Service.Devices.Firmware.DfuUtil(Nexus.Service.Devices.Firmware.DfuUtil.ResolveDefaultPath()));
        services.AddSingleton<Nexus.Service.Devices.Firmware.WinUsbDriverInstaller>();
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Cnvs.CnvsHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Np50.Np50Hub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.IDfuFlashTarget>(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHub>());
        services.AddSingleton<Nexus.Service.Devices.Firmware.FirmwareFlasher>();

        // NP50 hub: serial port discovery + transport factory + singleton hub +
        // 2-second heartbeat poller. Discovery is Windows-only for now; non-
        // Windows builds get a stub that finds nothing (the hub silently stays
        // disconnected, which keeps the rest of the service composing cleanly).
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                              Nexus.Service.Peripherals.Hyte.Np50.WindowsNp50PortDiscovery>();
#else
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                                  Nexus.Service.Peripherals.Hyte.Np50.LinuxNp50PortDiscovery>();
        }
        else
        {
            services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery,
                                  Nexus.Service.Peripherals.Hyte.Np50.StubNp50PortDiscovery>();
        }
#endif
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.Np50Hub>(sp =>
            new Nexus.Service.Peripherals.Hyte.Np50.Np50Hub(
                sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery>(),
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial)));
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Np50.Np50HeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Np50.Np50HeartbeatWorker>());

        // MiniHub hub: own port-discovery instance (we don't bind it to
        // INp50PortDiscovery in DI because that interface is already taken
        // by the NP50 binding — instead the hub factory below constructs
        // the MiniHub-specific discovery inline). Transport factory reuses
        // the generic serial-port wrapper since it's product-agnostic.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.Np50.INp50PortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.MiniHub.WindowsMiniHubPortDiscovery();
#else
            discovery = OperatingSystem.IsLinux()
                ? new Nexus.Service.Peripherals.Hyte.MiniHub.LinuxMiniHubPortDiscovery()
                : new Nexus.Service.Peripherals.Hyte.MiniHub.StubMiniHubPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker>());

        // Q-series cooler controller (Q60 / Q80): serial-over-USB hub mirroring
        // the MiniHub stack. Reads firmware version + variant so the Firmware
        // Updates page can show current-vs-available. Reuses the product-agnostic
        // Np50SerialTransport; discovery is Windows-only (stub elsewhere).
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.QSeriesCooler.IQSeriesCoolerPortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.QSeriesCooler.WindowsQSeriesCoolerPortDiscovery();
#else
            discovery = OperatingSystem.IsLinux()
                ? new Nexus.Service.Peripherals.Hyte.QSeriesCooler.LinuxQSeriesCoolerPortDiscovery()
                : new Nexus.Service.Peripherals.Hyte.QSeriesCooler.StubQSeriesCoolerPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHeartbeatWorker>());

        // Y70 Touch display controller (Touch / Infinite / Truly): serial-over-USB
        // hub mirroring the Q-series stack. Reads firmware version + variant for the
        // Firmware Updates page. Reuses Np50SerialTransport; discovery is Windows-only.
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHub>(sp =>
        {
            Nexus.Service.Peripherals.Hyte.Y70Display.IY70DisplayPortDiscovery discovery;
#if WINDOWS
            discovery = new Nexus.Service.Peripherals.Hyte.Y70Display.WindowsY70DisplayPortDiscovery();
#else
            discovery = OperatingSystem.IsLinux()
                ? new Nexus.Service.Peripherals.Hyte.Y70Display.LinuxY70DisplayPortDiscovery()
                : new Nexus.Service.Peripherals.Hyte.Y70Display.StubY70DisplayPortDiscovery();
#endif
            return new Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHub(
                discovery,
                port => new Nexus.Service.Peripherals.Hyte.Np50.Np50SerialTransport(port.PortName, port.Serial));
        });
        services.AddSingleton<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHeartbeatWorker>();
        services.AddHostedService(sp =>
            sp.GetRequiredService<Nexus.Service.Peripherals.Hyte.Y70Display.Y70DisplayHeartbeatWorker>());

#if WINDOWS
        services.AddSingleton<Nexus.Service.Devices.Detection.WindowsUsbEnumerator>();
        services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
            new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                sp.GetRequiredService<Nexus.Service.Devices.Detection.WindowsUsbEnumerator>()));
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<Nexus.Service.Devices.Detection.MacUsbEnumerator>();
            services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
                new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                    sp.GetRequiredService<Nexus.Service.Devices.Detection.MacUsbEnumerator>()));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Nexus.Service.Devices.Detection.LinuxUsbEnumerator>();
            services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
                new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                    sp.GetRequiredService<Nexus.Service.Devices.Detection.LinuxUsbEnumerator>()));
        }
        else
        {
            services.AddSingleton<Nexus.Service.Devices.Detection.StubUsbEnumerator>();
            services.AddSingleton<Nexus.Service.Devices.Detection.IUsbEnumerator>(sp =>
                new Nexus.Service.Devices.Detection.CachingUsbEnumerator(
                    sp.GetRequiredService<Nexus.Service.Devices.Detection.StubUsbEnumerator>()));
        }
#endif
        services.AddSingleton<DeviceManager>();
        services.AddSingleton<Nexus.Service.Devices.DeviceBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Devices.DeviceBroadcaster>());
        return services;
    }

    public static IServiceCollection AddNexusPeripherals(this IServiceCollection services)
    {
#if WINDOWS
        services.AddSingleton<Nexus.Service.Peripherals.Hid.IHidEnumerator, Nexus.Service.Peripherals.Hid.WindowsHidEnumerator>();
#else
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<Nexus.Service.Peripherals.Hid.IHidEnumerator, Nexus.Service.Peripherals.Hid.LinuxHidEnumerator>();
        }
        else
        {
            services.AddSingleton<Nexus.Service.Peripherals.Hid.IHidEnumerator, Nexus.Service.Peripherals.Hid.StubHidEnumerator>();
        }
#endif
        services.AddSingleton<Nexus.Service.Peripherals.PeripheralRegistry>();

        // Real HID-backed keeb provider. StubKeebProvider stays registered only
        // as the IInputterProvider fallback on platforms without a native inputter.
        services.AddSingleton<StubKeebProvider>();
        services.AddSingleton<RealKeebProvider>();
        services.AddSingleton<IKeebProvider>(sp => sp.GetRequiredService<RealKeebProvider>());
#if WINDOWS
        services.AddSingleton<IInputterProvider, WindowsInputter>();
#else
        if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IInputterProvider, LinuxInputter>();
        }
        else
        {
            services.AddSingleton<IInputterProvider>(sp => sp.GetRequiredService<StubKeebProvider>());
        }
#endif

        // Real Y70 control (serial brightness/power + DDC/CI fallback). Degrades
        // to persist-only when no panel is attached (hub disconnected + no DDC
        // match), so it composes cleanly cross-platform without the old stub.
        services.AddSingleton<IY70Provider, Y70Provider>();
        services.AddSingleton<IQSeriesProvider, StubQSeriesProvider>();

#if WINDOWS
        // Brightness proxies through the user-session helper - DDC/CI and
        // laptop-panel APIs are unreliable from Session 0.
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
            Nexus.Service.Platform.Displays.HelperDisplayBrightnessProxy>();
        // Y70 display rotation also routes through the helper: Session 0
        // cannot ChangeDisplaySettingsEx against the user's monitors.
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayOrientationProvider,
            Nexus.Service.Platform.Displays.HelperDisplayOrientationProxy>();
        // Monitor enumeration follows the same Session 0 limitation: DXGI
        // EnumOutputs returns nothing under LocalSystem, so the helper does
        // the enumeration and we proxy.
        services.AddSingleton<Nexus.Service.Platform.IMonitorEnumerator,
            Nexus.Service.Platform.Displays.HelperMonitorEnumeratorProxy>();
        // Screen-mirror frames also flow through the helper - DXGI desktop
        // duplication is Session 0-blind, so the helper captures + downsamples
        // and pushes canvas-resolution RGB24 over the pipe.
        services.AddSingleton<Nexus.Service.Lighting.Capture.IScreenFrameSource,
            Nexus.Service.Lighting.Capture.HelperScreenFrameSource>();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
                Nexus.Service.Platform.Displays.MacDisplayBrightnessProvider>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
                Nexus.Service.Platform.Displays.LinuxDisplayBrightnessProvider>();
            // Screen-mirror frames come from the xdg-desktop-portal ScreenCast
            // portal (PipeWire), consumed by a gst-launch reader. The portal
            // handshake rides the session D-Bus connection (AddNexusLinuxDBus);
            // without this binding the effect's IScreenFrameSource stays null and
            // screen-mirror renders nothing on Linux.
            services.AddSingleton<Nexus.Service.Lighting.Capture.IScreenFrameSource,
                Nexus.Service.Lighting.Capture.LinuxScreenFrameSource>();
        }
        else
        {
            services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayBrightnessProvider,
                Nexus.Service.Platform.Displays.StubDisplayBrightnessProvider>();
        }
        services.AddSingleton<Nexus.Service.Platform.IMonitorEnumerator,
            Nexus.Service.Platform.DefaultMonitorEnumerator>();
        services.AddSingleton<Nexus.Service.Platform.Displays.IDisplayOrientationProvider,
            Nexus.Service.Platform.Displays.NoopDisplayOrientationProvider>();
#endif
        services.AddSingleton<Nexus.Service.Platform.Displays.DisplayBrightnessController>();
        return services;
    }

    public static IServiceCollection AddNexusActivity(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Activity.Storage.IScreenTimeStore>(_ =>
        {
            try
            {
                return new Nexus.Service.Activity.Storage.SqliteScreenTimeStore();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[screentime-store] sqlite unavailable, using in-memory: {ex.Message}");
                return new Nexus.Service.Activity.Storage.InMemoryScreenTimeStore();
            }
        });

#if WINDOWS
        services.AddSingleton<IScreenTimeProvider, WindowsScreenTimeProvider>();
        services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
        services.AddSingleton<IShortcutsProvider, WindowsShortcutsProvider>();
        services.AddSingleton<IMediaProvider, WindowsMediaProvider>();
        services.AddSingleton<IVolumeProvider, WindowsVolumeProvider>();
        services.AddSingleton<IBeatsProvider, WasapiLoopbackBeatsProvider>();
#else
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IScreenTimeProvider, MacScreenTimeProvider>();
            services.AddSingleton<IAppDetectionProvider, MacAppDetectionProvider>();
            services.AddSingleton<IShortcutsProvider, MacShortcutsProvider>();
            services.AddSingleton<IMediaProvider, MacMediaProvider>();
            services.AddSingleton<IVolumeProvider, MacVolumeProvider>();
            services.AddSingleton<IBeatsProvider, MacAudioBeatsProvider>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Nexus.Service.Activity.LinuxScreenTimeProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Activity.LinuxScreenTimeProvider>());
            services.AddSingleton<IScreenTimeProvider>(sp => sp.GetRequiredService<Nexus.Service.Activity.LinuxScreenTimeProvider>());
            services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
            services.AddSingleton<IShortcutsProvider, LinuxShortcutsProvider>();
            services.AddSingleton<IMediaProvider, LinuxMediaProvider>();
            services.AddSingleton<IVolumeProvider, LinuxVolumeProvider>();
            services.AddSingleton<IBeatsProvider, BeatsProvider>();
        }
        else
        {
            services.AddSingleton<IScreenTimeProvider, StubScreenTimeProvider>();
            services.AddSingleton<IAppDetectionProvider, StubAppDetectionProvider>();
            services.AddSingleton<IShortcutsProvider, StubShortcutsProvider>();
            services.AddSingleton<IMediaProvider, StubMediaProvider>();
            services.AddSingleton<IVolumeProvider, StubVolumeProvider>();
            services.AddSingleton<IBeatsProvider, StubBeatsProvider>();
        }
#endif
        return services;
    }

    public static IServiceCollection AddNexusNetwork(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<MacNetworkProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<MacNetworkProvider>());
            services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<MacNetworkProvider>());
        }
#if WINDOWS
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<WindowsNetworkProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<WindowsNetworkProvider>());
            services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<WindowsNetworkProvider>());
        }
#endif
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<LinuxNetworkProvider>();
            services.AddHostedService(sp => sp.GetRequiredService<LinuxNetworkProvider>());
            services.AddSingleton<INetworkProvider>(sp => sp.GetRequiredService<LinuxNetworkProvider>());
        }
        else
        {
            services.AddSingleton<INetworkProvider, StubNetworkProvider>();
        }
        return services;
    }

    public static IServiceCollection AddNexusLifecycle(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            services.AddSingleton<IStartupProvider, MacStartupProvider>();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            services.AddSingleton<IStartupProvider, WindowsStartupProvider>();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            services.AddSingleton<IStartupProvider, LinuxStartupProvider>();
        else
            services.AddSingleton<IStartupProvider, StubStartupProvider>();

        services.AddSingleton<IShutdownProvider, StubShutdownProvider>();
#if WINDOWS
        services.AddSingleton<IPawnIoProvider, PawnIoProvider>();
#else
        services.AddSingleton<IPawnIoProvider, StubPawnIoProvider>();
#endif

        // Replay persisted lighting + cooling state to hardware on startup.
        // Lives in Lifecycle because it doesn't belong to a single domain.
        services.AddHostedService<AutoRestoreOnStart>();
        return services;
    }

    public static IServiceCollection AddNexusBenchmarks(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Benchmarks.IBenchmarkProvider, Nexus.Service.Benchmarks.Providers.DefaultBenchmarkProvider>();
        services.AddSingleton<Nexus.Service.Benchmarks.BenchmarkRunner>();
        services.AddSingleton<ProfileManager>();
        services.AddSingleton<Nexus.Service.Media.MediaLibrary>();
        return services;
    }

    public static IServiceCollection AddNexusWeather(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Platform.Weather.IWeatherProvider, Nexus.Service.Platform.Weather.OpenMeteoWeatherProvider>();
        return services;
    }

    /// <summary>
    /// Widget runtime. Phase 0 only registers discovery; serving routes are
    /// wired in <see cref="Nexus.Service.Routes.WidgetRoutes.MapWidgetEndpoints"/>
    /// in Program.cs.
    /// </summary>
    public static IServiceCollection AddNexusWidgets(this IServiceCollection services)
    {
        services.AddSingleton<Nexus.Service.Widgets.WidgetRegistry>();
        services.AddSingleton<Nexus.Service.Widgets.WidgetSettingsService>();
        services.AddSingleton<Nexus.Service.Widgets.WidgetProxyService>();
        services.AddSingleton<Nexus.Service.Widgets.WidgetInstaller>();
        services.AddSingleton<Nexus.Service.Widgets.WidgetCodeSessionService>();
        services.AddSingleton<Nexus.Service.Widgets.WidgetActionRegistry>(sp =>
        {
            var registry = new Nexus.Service.Widgets.WidgetActionRegistry();
            // First-party action modules. Each registers its own actions
            // by name; the manifest's capabilities.dispatch allowlist
            // gates per-widget access.
            Nexus.Service.Widgets.WidgetActions.DisplayActions.RegisterAll(registry);
            Nexus.Service.Widgets.WidgetActions.ScreentimeActions.RegisterAll(registry);
            Nexus.Service.Widgets.WidgetActions.MacroActions.RegisterAll(registry);
            return registry;
        });
        return services;
    }

    public static IServiceCollection AddNexusPanel(this IServiceCollection services, int servicePort)
    {
        // QSeriesPortWatcher keeps `adb reverse tcp:{servicePort}` alive
        // while a HYTE Q60 / Q80 USB display is attached. Without it,
        // every time Y70's adb-server restarts the panel's multiplex
        // WebSocket on the Q-series silently freezes. Windows-only — the
        // Q-series host stack lives on the Y70 PC.
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.QSeries.QSeriesPortWatcher>(
                _ => new Nexus.Service.QSeries.QSeriesPortWatcher(servicePort));
            services.AddHostedService(sp =>
                sp.GetRequiredService<Nexus.Service.QSeries.QSeriesPortWatcher>());
        }

        services.AddSingleton<Nexus.Service.Panel.PanelKioskLauncher>();
        services.AddSingleton<Nexus.Service.Panel.PanelOverlayHostLauncher>();
        // IOverlayHost picks the right impl per OS. Mac spawns the Swift
        // sidecar nexus-overlay-helper (transparent NSWindow + WKWebView
        // per NSScreen). Windows spawns nexus-overlay.exe (WinForms +
        // WebView2). Linux is a no-op until an X11/Wayland surface is added.
        if (OperatingSystem.IsMacOS())
        {
            Nexus.Service.Platform.Mac.MacOverlayHostLauncher.Configure(servicePort);
            services.AddSingleton<Nexus.Service.Panel.IOverlayHost, Nexus.Service.Platform.Mac.MacOverlayHostLauncher>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.Panel.IOverlayHost>(sp =>
                sp.GetRequiredService<Nexus.Service.Panel.PanelOverlayHostLauncher>());
        }
        else
        {
            services.AddSingleton<Nexus.Service.Panel.IOverlayHost, Nexus.Service.Panel.NoopOverlayHost>();
        }
        services.AddSingleton<Nexus.Service.Panel.PanelPhonePairingService>();
        services.AddSingleton<Nexus.Service.Panel.PanelDeviceRegistry>();
        return services;
    }

    public static IServiceCollection AddNexusLinuxDBus(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<Nexus.Service.Platform.Linux.DBus.DBusConnection>();
            services.AddHostedService<Nexus.Service.Platform.Linux.LinuxTrayService>();
        }
        return services;
    }

    /// <summary>
    /// Windows user-session helper IPC. The named-pipe server, registry, and
    /// typed command client. Other platforms run their providers natively in
    /// the user-context daemon, so no helper subsystem is registered.
    /// </summary>
    public static IServiceCollection AddNexusHelper(this IServiceCollection services)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<Nexus.Service.Helper.HelperRegistry>();
            services.AddSingleton<Nexus.Service.Helper.HelperPipeServer>();
            services.AddHostedService(sp => sp.GetRequiredService<Nexus.Service.Helper.HelperPipeServer>());
        }
#endif
        return services;
    }
}
