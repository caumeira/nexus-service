using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// Integration classes that boot a <see cref="NexusAppFactory"/> host join this
/// collection so only one in-process host runs at a time. Several hosts booting
/// in parallel contend on process-global state (the Console redirection in
/// ServiceLog.Initialize, the boot timer) and can deadlock the run. The rest of
/// the suite still parallelizes normally.
/// </summary>
[CollectionDefinition("NexusHost", DisableParallelization = true)]
public sealed class NexusHostCollection { }

/// <summary>
/// In-process integration host. Boots the real <c>Program.cs</c> request
/// pipeline - middleware order, CORS, SecurityHeaders, PathAuth, every mapped
/// route, the source-generated JSON, and the WebSocket hubs - against an
/// in-memory TestServer. No Kestrel, no hardware, and no machine-mutating boot
/// side effects (single-instance mutex, HTTPS cert provisioning, GPU/profile
/// init, orphan-process cleanup, OS protocol-handler registration, GUI/service
/// host) - all gated by <c>NEXUS_TEST_HOST</c> in Program.cs.
///
/// Background <see cref="IHostedService"/>s (mDNS advertiser, curve engine,
/// device/serial/USB watchers, monitoring broadcaster) are stripped so request
/// handling is deterministic; a test that needs one re-adds it through
/// <see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>.
/// </summary>
public class NexusAppFactory : WebApplicationFactory<Program>
{
    private readonly string _configDir;

    /// <summary>Per-factory settings.json path (temp). Survives for the host's lifetime.</summary>
    public string SettingsPath { get; }

    public NexusAppFactory()
    {
        // Must be set before the base class lazily starts the host (on first
        // CreateClient / Services access) so Program.cs's pre-build gates -
        // single-instance mutex and cert provisioning - observe it.
        Environment.SetEnvironmentVariable("NEXUS_TEST_HOST", "1");

        _configDir = Path.Combine(Path.GetTempPath(), "nexus-itest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        SettingsPath = Path.Combine(_configDir, "settings.json");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // The request pipeline under test does not depend on any hosted
            // service being started, and they touch hardware/network and add
            // timing nondeterminism - so strip them all.
            services.RemoveAll<IHostedService>();

            // Isolate config (the auth token + all settings) to a per-factory
            // temp file so integration tests never read or mutate the
            // developer's real settings.json.
            services.RemoveAll<IConfigStore>();
            services.AddSingleton<IConfigStore>(new JsonConfigStore(SettingsPath));

            // IMcpAuditSink resolves McpServerHost -> McpToolRegistry ->
            // IAiHistoryStore even with hosted services stripped, since /ai/*
            // route handlers take McpServerHost as a DI param. Isolate its
            // SQLite file to the same per-factory temp dir so an AI-route test
            // never touches the developer's real machine data directory.
            services.RemoveAll<Nexus.Service.Mcp.History.IAiHistoryStore>();
            services.AddSingleton<Nexus.Service.Mcp.History.IAiHistoryStore>(
                new Nexus.Service.Mcp.History.SqliteAiHistoryStore(Path.Combine(_configDir, "ai-history.db")));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_configDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
