using System;
using System.Threading;
using System.Threading.Tasks;
using Qos.Service.Platform.Linux.DBus;
using Microsoft.Extensions.Hosting;

namespace Qos.Service.Platform.Linux;

/// <summary>
/// Spins up the KDE tray icon alongside the web service. Consumes the shared
/// <see cref="DBusConnection"/> singleton. Fail-soft: if D-Bus is unavailable
/// (headless / non-KDE), logs and keeps the web service running.
/// </summary>
public sealed class LinuxTrayService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DBusConnection _dbus;
    private LinuxTrayHost? _host;

    public LinuxTrayService(IHostApplicationLifetime lifetime, DBusConnection dbus)
    {
        _lifetime = lifetime;
        _dbus = dbus;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var url = ResolveUrl();
        try
        {
            await _dbus.StartAsync();
            _host = new LinuxTrayHost(_dbus, url, () => _lifetime.StopApplication());
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] disabled: {ex.Message}");
            _host?.Dispose();
            _host = null;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _host?.Dispose();
        _host = null;
        return Task.CompletedTask;
    }

    private static string ResolveUrl()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return a.TrimEnd('/');
            }
        }
        return "http://localhost:9400";
    }
}
