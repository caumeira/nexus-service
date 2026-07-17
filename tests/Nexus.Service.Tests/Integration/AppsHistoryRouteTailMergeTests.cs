using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Monitoring.History;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET /monitoring/history/apps against a fake IAppUsageHistoryStore (db
/// side) plus the real AppSampleBuffer singleton (tail side), verifying the
/// route merges the two rather than only ever answering from the db - the
/// gap a prior review round found (the apps route ignored the unflushed
/// tail entirely, so its right edge disagreed with /monitoring/history's).
/// </summary>
[Collection("NexusHost")]
public sealed class AppsHistoryRouteTailMergeTests : IDisposable
{
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly FakeAppUsageHistoryStore _store = new();

    public AppsHistoryRouteTailMergeTests()
    {
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAppUsageHistoryStore>();
                services.AddSingleton<IAppUsageHistoryStore>(_store);
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

    private sealed class FakeAppUsageHistoryStore : IAppUsageHistoryStore
    {
        public List<AppWindowStat> TopApps = new();
        public Dictionary<string, List<AppRawPoint>> Series = new(StringComparer.OrdinalIgnoreCase);
        public List<long> SampledTicks = new();

        public void Append(IReadOnlyList<AppUsageTick> ticks, long? pruneCutoffSec) { }

        public IReadOnlyList<AppWindowStat> QueryTopApps(string metric, long fromSec, long toSec, int maxApps) =>
            TopApps.OrderByDescending(a => a.Avg).Take(maxApps).ToList();

        public IReadOnlyList<AppRawPoint> QueryAppSeries(string metric, string appName, long fromSec, long toSec) =>
            Series.TryGetValue(appName, out var points) ? points : Array.Empty<AppRawPoint>();

        public IReadOnlyList<long> QuerySampledTicks(string metric, long fromSec, long toSec) => SampledTicks;
    }

    [Fact]
    public async Task TailOnlyApp_WithNoDbHistory_AppearsInTheResponse()
    {
        // app.exe has been running only for the last few seconds - never
        // flushed to the db, only visible in the buffered tail.
        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000,
            new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 80, null) }) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var apps = doc.RootElement.GetProperty("apps");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("app.exe", apps[0].GetProperty("name").GetString());
        Assert.Equal(80, apps[0].GetProperty("avg").GetDouble());
    }

    [Fact]
    public async Task TailSample_MergesWithDbHistory_ForTheSameApp()
    {
        _store.SampledTicks = new List<long> { 1000 };
        _store.TopApps = new List<AppWindowStat> { new("app.exe", 20, 20) };
        _store.Series["app.exe"] = new List<AppRawPoint> { new(1000, 20, null) };

        var appBuffer = _factory.Services.GetRequiredService<AppSampleBuffer>();
        appBuffer.Append(new AppUsageTick(5000,
            new[] { new AppMetricSample("cpu", new[] { new AppUsagePoint("app.exe", 60, null) }) }));

        var res = await Client().GetAsync("/monitoring/history/apps?from=0&to=6000000&series=cpu");

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var app = doc.RootElement.GetProperty("apps")[0];
        Assert.Equal("app.exe", app.GetProperty("name").GetString());
        // Two sampled ticks total (db=1000, tail=5000): (20+60)/2 = 40.
        Assert.Equal(40, app.GetProperty("avg").GetDouble());
        Assert.Equal(2, app.GetProperty("points").GetArrayLength());
    }
}
