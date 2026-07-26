using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class InstallIdentityTests
{
    [Fact]
    public void Resolve_generates_persists_and_is_stable_when_enabled()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.CollectAnonymousData = true);

        var id = InstallIdentity.Resolve(store);
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(id, store.Load().Telemetry.InstallId);
        Assert.Equal(id, InstallIdentity.Resolve(store)); // same id on the next call
    }

    [Fact]
    public void Resolve_returns_null_but_keeps_id_when_opted_out()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "old-id";
        });

        Assert.Null(InstallIdentity.Resolve(store));
        Assert.Equal("old-id", store.Load().Telemetry.InstallId);
    }

    [Fact]
    public void ResolveStored_returns_null_when_nothing_was_ever_minted()
    {
        var store = new InMemoryConfigStore();
        Assert.Null(InstallIdentity.ResolveStored(store));
        Assert.Equal("", store.Load().Telemetry.InstallId); // never mints one itself
    }

    [Fact]
    public void ResolveStored_reads_the_id_regardless_of_consent()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = true;
            s.Telemetry.InstallId = "existing-id";
        });
        Assert.Equal("existing-id", InstallIdentity.ResolveStored(store));

        store.Update(s => s.Telemetry.CollectAnonymousData = false);
        Assert.Equal("existing-id", InstallIdentity.ResolveStored(store));
    }
}
