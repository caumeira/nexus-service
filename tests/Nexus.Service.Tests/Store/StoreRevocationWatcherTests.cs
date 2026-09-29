using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Store;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Store;

public class StoreRevocationWatcherTests
{
    private readonly List<AppEntry> _installed = new();
    private readonly List<string> _uninstalled = new();
    private int _announced;

    private StoreRevocationWatcher Watcher(string? body) => new(
        _ => Task.FromResult(body),
        () => _installed,
        (id, _) => { _uninstalled.Add(id); return Task.FromResult(true); },
        () => _announced++);

    private void Installed(string id, string version, AppInstallPaths.Source source = AppInstallPaths.Source.User) =>
        _installed.Add(new AppEntry { Id = id, RootPath = "/apps/" + id, Source = source, Manifest = new AppManifest { Id = id, Version = version } });

    [Fact]
    public async Task UninstallsOnlyTheExactRevokedVersionOfAStoreInstall()
    {
        Installed("com.x.bad", "1.2.0");
        Installed("com.x.fine", "1.3.0");
        Installed("com.x.bundled", "1.2.0", AppInstallPaths.Source.Bundled);

        var removed = await Watcher("""{"revoked":[{"appId":"com.x.bad","version":"1.2.0"},{"appId":"com.x.fine","version":"1.2.0"},{"appId":"com.x.bundled","version":"1.2.0"}]}""")
            .TickAsync(CancellationToken.None);

        Assert.Equal(new[] { "com.x.bad" }, removed);
        Assert.Equal(new[] { "com.x.bad" }, _uninstalled);
        Assert.Equal(1, _announced);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"revoked":[]}""")]
    public async Task AnUnreachableOrEmptyListChangesNothing(string? body)
    {
        Installed("com.x.app", "1.0.0");

        Assert.Empty(await Watcher(body).TickAsync(CancellationToken.None));
        Assert.Empty(_uninstalled);
        Assert.Equal(0, _announced);
    }
}
