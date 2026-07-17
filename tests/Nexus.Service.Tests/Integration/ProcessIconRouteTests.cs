using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// The /monitoring/process-icon route's resolve/cache/ETag contract,
/// exercised through the real pipeline against a fake IProcessIconProvider so
/// this runs on every platform - the real Windows extraction pipeline
/// (WindowsIconExtractor, running in the user-session helper) is Windows-only
/// and has no portable unit test, same posture as the sibling
/// ShortcutsIconRouteTests.
/// </summary>
[Collection("NexusHost")]
public sealed class ProcessIconRouteTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeProcessIconProvider _icons = new();

    public ProcessIconRouteTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProcessIconProvider>();
                services.AddSingleton<IProcessIconProvider>(_icons);
            }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    // ResolveExecutablePath re-resolves the pid via Process.GetProcessById, so
    // the seeded pid must be a real running process - the test process itself.
    private void SeedProcess(string name)
    {
        var processes = _factory.Services.GetRequiredService<ProcessMonitor>();
        processes.SetProcessesForTest(new[]
        {
            new ProcessInfo { Pid = Environment.ProcessId, Name = name, CpuPercent = 1, MemoryMb = 10 },
        });
    }

    private sealed class FakeProcessIconProvider : IProcessIconProvider
    {
        public byte[]? IconBytes = Array.Empty<byte>();
        public int CallCount;

        public byte[]? GetIcon(string exePath)
        {
            CallCount++;
            return IconBytes;
        }
    }

    [Fact]
    public async Task MissingName_Is400()
    {
        var res = await Client().GetAsync("/monitoring/process-icon");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task NoLiveProcessMatchesTheName_Is404()
    {
        var res = await Client().GetAsync("/monitoring/process-icon?name=never-seen.exe");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task EmptyIconFromProvider_Is404()
    {
        SeedProcess("app.exe");
        _icons.IconBytes = Array.Empty<byte>();

        var res = await Client().GetAsync("/monitoring/process-icon?name=app.exe");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task EmptyIconFromProvider_IsCached_ProviderNotCalledAgain()
    {
        SeedProcess("app.exe");
        _icons.IconBytes = Array.Empty<byte>();
        var client = Client();

        await client.GetAsync("/monitoring/process-icon?name=app.exe");
        await client.GetAsync("/monitoring/process-icon?name=app.exe");

        Assert.Equal(1, _icons.CallCount); // an empty result is a real negative, safe to cache
    }

    [Fact]
    public async Task IconPresent_Returns200_WithEtagAndCacheControl()
    {
        SeedProcess("app.exe");
        _icons.IconBytes = new byte[] { 1, 2, 3, 4, 5 };

        var res = await Client().GetAsync("/monitoring/process-icon?name=app.exe");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(_icons.IconBytes, await res.Content.ReadAsByteArrayAsync());
        Assert.NotNull(res.Headers.ETag);
        Assert.NotNull(res.Headers.CacheControl);
    }

    [Fact]
    public async Task RepeatedRequest_ServesFromServerSideCache_WithoutCallingTheProviderAgain()
    {
        SeedProcess("app.exe");
        _icons.IconBytes = new byte[] { 9, 9, 9 };
        var client = Client();

        await client.GetAsync("/monitoring/process-icon?name=app.exe");
        await client.GetAsync("/monitoring/process-icon?name=app.exe");

        Assert.Equal(1, _icons.CallCount);
    }

    [Fact]
    public async Task NullFromProvider_TransportFailure_Is404_ButNotCached()
    {
        SeedProcess("app.exe");
        _icons.IconBytes = null;
        var client = Client();

        var first = await client.GetAsync("/monitoring/process-icon?name=app.exe");
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        _icons.IconBytes = new byte[] { 7, 7, 7 };
        var second = await client.GetAsync("/monitoring/process-icon?name=app.exe");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, _icons.CallCount); // never cached the null, so the provider is retried
    }

    [Fact]
    public async Task MatchingIfNoneMatch_Returns304_WithEmptyBody()
    {
        SeedProcess("app.exe");
        _icons.IconBytes = new byte[] { 9, 9, 9 };
        var client = Client();

        var first = await client.GetAsync("/monitoring/process-icon?name=app.exe");
        var etag = first.Headers.ETag!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/monitoring/process-icon?name=app.exe");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }
}
