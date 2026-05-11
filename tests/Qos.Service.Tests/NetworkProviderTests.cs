using Qos.Service.Activity;
using Qos.Service.Models.Activity;
using Qos.Service.Sockets;

namespace Qos.Service.Tests;

public class NetworkProviderTests
{
    [Fact]
    public void StubProvider_GetSnapshot_ReturnsEmpty()
    {
        var provider = new StubNetworkProvider();

        var snapshot = provider.GetSnapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void MacNetworkProvider_GetSnapshot_InitiallyEmpty()
    {
        var provider = new MacNetworkProvider(new MultiplexHub());

        // Before ExecuteAsync runs, snapshot should be empty
        var snapshot = provider.GetSnapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void MacNetworkProvider_GetSnapshot_IsReadOnly()
    {
        var provider = new MacNetworkProvider(new MultiplexHub());

        var snapshot = provider.GetSnapshot();

        Assert.IsAssignableFrom<IReadOnlyList<NetworkProcessInfo>>(snapshot);
    }
}
