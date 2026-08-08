using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Subscribes to OS power events and bounces the OpenRGB subprocess when the
/// system resumes from sleep so devices re-init after the USB stack
/// re-enumerates.
///
/// Windows: hooks <c>SystemEvents.PowerModeChanged</c> via Microsoft.Win32.
/// Linux has its own equivalent, <see cref="Nexus.Service.Platform.Linux.LinuxResumeListener"/>,
/// subscribed to logind over D-Bus. macOS is a no-op here (no resume hook wired yet).
/// </summary>
public sealed class PowerEventListener : IHostedService, IDisposable
{
    private readonly RgbBridge _bridge;
#if WINDOWS
    private bool _subscribed;
#endif

    public PowerEventListener(RgbBridge bridge)
    {
        _bridge = bridge;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                _subscribed = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[power-events] failed to subscribe: {ex.Message}");
            }
        }
#endif
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Console.Error.WriteLine("[power-events] system resumed - bouncing OpenRGB subprocess");
            _bridge.OnSystemResume();
        }
    }
#endif

    private void Unsubscribe()
    {
#if WINDOWS
        if (_subscribed && OperatingSystem.IsWindows())
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            _subscribed = false;
        }
#endif
    }

    public void Dispose() => Unsubscribe();
}
