using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Linux.DBus;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Spins up the KDE/SNI tray icon alongside the web service. Consumes the shared
/// <see cref="DBusConnection"/> singleton. Fail-soft: if D-Bus is unavailable
/// (headless / non-KDE), logs and keeps the web service running.
///
/// Windows-tray parity additions: surfaces a desktop notification when a phone
/// pairing request needs attention, and re-publishes the tray after the session
/// bus drops and reconnects (logout/relogin, bus restart) — the SNI registration
/// dies with the old connection otherwise.
/// </summary>
public sealed class LinuxTrayService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DBusConnection _dbus;
    private readonly PanelPhonePairingService _pairing;
    private LinuxTrayHost? _host;
    private string _url = "http://localhost:9400";

    public LinuxTrayService(IHostApplicationLifetime lifetime, DBusConnection dbus, PanelPhonePairingService pairing)
    {
        _lifetime = lifetime;
        _dbus = dbus;
        _pairing = pairing;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _url = ResolveUrl();
        _dbus.Reconnected += OnReconnected;
        _pairing.PairRequestNeedsAttention += OnPairAttention;
        try
        {
            await _dbus.StartAsync();
            _host = new LinuxTrayHost(_dbus, _url, () => _lifetime.StopApplication());
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
        _dbus.Reconnected -= OnReconnected;
        _pairing.PairRequestNeedsAttention -= OnPairAttention;
        _host?.Dispose();
        _host = null;
        return Task.CompletedTask;
    }

    // Session bus came back — the old SNI registration died with it, so rebuild
    // the tray host on the fresh connection. Fire-and-forget.
    private void OnReconnected() => _ = ReRegisterAsync();

    private async Task ReRegisterAsync()
    {
        try
        {
            _host?.Dispose();
            _host = new LinuxTrayHost(_dbus, _url, () => _lifetime.StopApplication());
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tray] re-register after reconnect failed: {ex.Message}");
        }
    }

    private void OnPairAttention(PanelPhonePairingService.PairAttentionNotice notice)
        => LinuxNotify.Send("Nexus pairing request",
            $"{(string.IsNullOrWhiteSpace(notice.DeviceLabel) ? "A device" : notice.DeviceLabel)} wants to pair.");

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
