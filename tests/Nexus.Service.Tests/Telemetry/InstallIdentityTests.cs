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
    public void Resolve_returns_null_and_forgets_id_when_opted_out()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Telemetry.CollectAnonymousData = false;
            s.Telemetry.InstallId = "old-id";
        });

        Assert.Null(InstallIdentity.Resolve(store));
        Assert.Equal("", store.Load().Telemetry.InstallId);
    }
}
