using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Deck;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Handle over a class-shared integration host. <see cref="Dispose"/> is a
/// deliberate no-op: the <c>IClassFixture</c> owns the host, while the per-test
/// <c>using (factory)</c> blocks date from when every test booted its own.
/// Keeping that shape lets the host be shared without editing each test body.
/// </summary>
internal sealed class SharedHost : IDisposable
{
    public SharedHost(IServiceProvider services) => Services = services;

    public IServiceProvider Services { get; }

    public void Dispose() { }
}

/// <summary>Class-shared host for the main deck routes: the loopback filter,
/// a temp image cache, and a spy action executor the tests assert against.</summary>
public sealed class StreamDeckRouteHostFactory : NexusAppFactory
{
    /// <summary>Temp cache dir the routes write into; tests assert on its contents.</summary>
    public string ImageCacheDir { get; } =
        Path.Combine(Path.GetTempPath(), "nexus-streamdeck-route-cache-" + Guid.NewGuid().ToString("N")[..8]);

    internal SpyDeckActionExecutor Executor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddTransient<IStartupFilter, LoopbackConnectionFilter>();
            s.RemoveAll<StreamDeckImageCache>();
            s.AddSingleton(new StreamDeckImageCache(ImageCacheDir));
            s.RemoveAll<IDeckActionExecutor>();
            s.AddSingleton<IDeckActionExecutor>(Executor);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(ImageCacheDir, recursive: true); } catch { /* best effort */ }
        }
    }
}

/// <summary>Class-shared host for the deck Presets routes: the loopback filter
/// every /streamdeck/* route needs, plus a temp image cache.</summary>
public sealed class StreamDeckPresetHostFactory : NexusAppFactory
{
    /// <summary>Temp cache dir the routes write into; tests assert on its contents.</summary>
    public string ImageCacheDir { get; } =
        Path.Combine(Path.GetTempPath(), "nexus-streamdeck-preset-cache-" + Guid.NewGuid().ToString("N")[..8]);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.AddTransient<IStartupFilter, LoopbackConnectionFilter>();
            s.RemoveAll<StreamDeckImageCache>();
            s.AddSingleton(new StreamDeckImageCache(ImageCacheDir));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(ImageCacheDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
