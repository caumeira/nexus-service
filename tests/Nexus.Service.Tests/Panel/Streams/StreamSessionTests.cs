using System.Linq;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public sealed class StreamSessionTests
{
    private static StreamSession NewSession(int fps = 10) => new(
        "session-1",
        new StreamedPanelDeviceInfo
        {
            Serial = "d211_test",
            Profile = new StreamedPanelProfile
            {
                Kind = "test",
                DisplayName = "Test",
                Surface = "monitor",
                CssWidth = 640,
                CssHeight = 480,
                Fps = fps,
            },
        },
        "panel-1");

    private static StreamFrame Frame(bool idr, byte marker = 0) => new()
    {
        Flags = idr ? StreamFraming.FlagIdr : (byte)0,
        Payload = new[] { marker },
    };

    [Fact]
    public void New_session_waits_for_idr_and_skips_to_first_idr()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: false, 1));
        session.Enqueue(Frame(idr: false, 2));
        session.Enqueue(Frame(idr: true, 3));
        session.Enqueue(Frame(idr: false, 4));

        var sent = session.DequeueForTick();

        Assert.Single(sent);
        Assert.Equal(3, sent[0].Payload[0]);
        Assert.True(sent[0].IsIdr);
    }

    [Fact]
    public void Mid_gop_frames_are_never_skipped_in_steady_state()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        Assert.Single(session.DequeueForTick());

        session.Enqueue(Frame(idr: false, 2));
        session.Enqueue(Frame(idr: false, 3));
        var first = session.DequeueForTick();
        var second = session.DequeueForTick();

        Assert.Equal(2, first.Single().Payload[0]);
        Assert.Equal(3, second.Single().Payload[0]);
    }

    [Fact]
    public void Overflow_trims_to_newest_idr()
    {
        // fps 10 -> cap floors at 30 queued frames.
        var session = NewSession(fps: 10);
        session.Enqueue(Frame(idr: true, 1));
        for (byte i = 2; i <= 28; i++) session.Enqueue(Frame(idr: false, i));
        session.Enqueue(Frame(idr: true, 29));
        session.Enqueue(Frame(idr: false, 30));
        Assert.Equal(30, session.QueueDepthForTest);

        session.Enqueue(Frame(idr: false, 31));

        Assert.Equal(3, session.QueueDepthForTest);
        var sent = session.DequeueForTick();
        Assert.Equal(29, sent[0].Payload[0]);
        Assert.True(sent[0].IsIdr);
    }

    [Fact]
    public void Overflow_with_no_idr_clears_and_rearms_resync()
    {
        var session = NewSession(fps: 10);
        session.Enqueue(Frame(idr: true, 1));
        Assert.Single(session.DequeueForTick());

        for (byte i = 0; i < 31; i++) session.Enqueue(Frame(idr: false, i));

        Assert.Equal(0, session.QueueDepthForTest);
        session.Enqueue(Frame(idr: false, 99));
        Assert.Empty(session.DequeueForTick());
        session.Enqueue(Frame(idr: true, 100));
        Assert.Equal(100, session.DequeueForTick().Single().Payload[0]);
    }

    [Fact]
    public void Transport_reopen_requires_fresh_idr()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        Assert.Single(session.DequeueForTick());

        session.RequireIdrResync();
        session.Enqueue(Frame(idr: false, 2));
        Assert.Empty(session.DequeueForTick());
        session.Enqueue(Frame(idr: true, 3));
        Assert.Equal(3, session.DequeueForTick().Single().Payload[0]);
    }

    [Fact]
    public void New_ingest_clears_queue_and_rearms_resync()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        session.Enqueue(Frame(idr: false, 2));

        session.ResetForNewIngest();

        Assert.Equal(0, session.QueueDepthForTest);
        Assert.Equal(StreamSessionState.Resync, session.State);
        session.SetTransportUp(true);
        Assert.Equal(StreamSessionState.Live, session.State);
        session.Enqueue(Frame(idr: false, 3));
        Assert.Empty(session.DequeueForTick());
    }

    [Fact]
    public void Closed_session_drops_enqueues()
    {
        var session = NewSession();
        session.Close();
        session.Enqueue(Frame(idr: true, 1));
        Assert.Equal(0, session.QueueDepthForTest);
        Assert.Equal(StreamSessionState.Closed, session.State);
    }

    [Fact]
    public void Catch_up_sends_two_per_tick_when_backlogged()
    {
        var session = NewSession();
        session.Enqueue(Frame(idr: true, 1));
        for (byte i = 2; i <= 9; i++) session.Enqueue(Frame(idr: false, i));

        var sent = session.DequeueForTick();

        Assert.Equal(2, sent.Count);
        Assert.Equal(1, sent[0].Payload[0]);
        Assert.Equal(2, sent[1].Payload[0]);
    }
}
