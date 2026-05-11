using System.Text;
using Qos.Service.Sockets;

namespace Qos.Service.Tests;

public class MultiplexHubSnapshotTests
{
    [Fact]
    public void TryGetTopicSnapshot_ReturnsFalseWhenNoProvider()
    {
        var hub = new MultiplexHub();

        var hit = hub.TryGetTopicSnapshot("screentime", out var envelope);

        Assert.False(hit);
        Assert.True(envelope.IsEmpty);
    }

    [Fact]
    public void TryGetTopicSnapshot_ReturnsProviderEnvelope()
    {
        var hub = new MultiplexHub();
        var bytes = Encoding.UTF8.GetBytes("{\"t\":\"volume\",\"d\":{\"volume\":0.5}}");
        hub.RegisterSnapshotProvider("volume", () => bytes);

        var hit = hub.TryGetTopicSnapshot("volume", out var envelope);

        Assert.True(hit);
        Assert.Equal(bytes, envelope.ToArray());
    }

    [Fact]
    public void TryGetTopicSnapshot_ReturnsFalseWhenProviderReturnsNull()
    {
        var hub = new MultiplexHub();
        hub.RegisterSnapshotProvider("screentime", () => null);

        var hit = hub.TryGetTopicSnapshot("screentime", out _);

        Assert.False(hit);
    }

    [Fact]
    public void TryGetTopicSnapshot_SwallowsProviderException()
    {
        var hub = new MultiplexHub();
        hub.RegisterSnapshotProvider("screentime", () => throw new InvalidOperationException("boom"));

        var hit = hub.TryGetTopicSnapshot("screentime", out _);

        Assert.False(hit);
    }

    [Fact]
    public void UnregisterSnapshotProvider_RemovesProvider()
    {
        var hub = new MultiplexHub();
        var bytes = new byte[] { 1, 2, 3 };
        hub.RegisterSnapshotProvider("volume", () => bytes);
        hub.UnregisterSnapshotProvider("volume");

        var hit = hub.TryGetTopicSnapshot("volume", out _);

        Assert.False(hit);
    }

    [Fact]
    public void RegisterSnapshotProvider_ReplacesPreviousProvider()
    {
        var hub = new MultiplexHub();
        hub.RegisterSnapshotProvider("volume", () => new byte[] { 1 });
        hub.RegisterSnapshotProvider("volume", () => new byte[] { 2 });

        hub.TryGetTopicSnapshot("volume", out var envelope);

        Assert.Equal(new byte[] { 2 }, envelope.ToArray());
    }
}
