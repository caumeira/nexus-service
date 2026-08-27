using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// <see cref="NexusAppFactory"/> with <see cref="ILightingDeviceProvider"/>
/// swapped for <see cref="StubDeviceProvider"/>, as an <c>IClassFixture</c> so a
/// class shares one host.
///
/// This replaces the <c>new NexusAppFactory().WithWebHostBuilder(...) as
/// NexusAppFactory ?? new NexusAppFactory()</c> idiom, which silently dropped
/// the override: <c>WithWebHostBuilder</c> returns the framework's internal
/// delegated factory, never the derived type, so the <c>as</c> cast was always
/// null and the <c>??</c> fallback handed back a plain, un-stubbed host.
/// Subclassing keeps the override on the same object.
/// </summary>
public sealed class StubDeviceHostFactory : NexusAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider>(sp =>
                new StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
        });
    }
}
