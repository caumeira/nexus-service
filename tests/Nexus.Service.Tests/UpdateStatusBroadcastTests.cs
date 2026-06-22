using System.Text;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public sealed class UpdateStatusBroadcastTests
{
    [Fact]
    public void BroadcastUpdate_NoSubscribers_DoesNotHitWire()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        PanelTopics.BroadcastUpdate(hub);

        Assert.Empty(captured);
    }

    [Fact]
    public void BroadcastUpdate_WithSubscriber_EmitsUpdateEnvelope()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(PanelTopics.Update);

        PanelTopics.BroadcastUpdate(hub);

        var frame = captured.Single(c => c.Topic == PanelTopics.Update);
        var json = Encoding.UTF8.GetString(frame.Payload);
        Assert.StartsWith("{\"t\":\"update\"", json);
        Assert.Contains("\"revision\":", json);
    }
}
