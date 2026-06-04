using Nexus.Service.Telemetry;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class TelemetryClientTests
{
    private static InMemoryConfigStore Store(bool collect)
    {
        var s = new InMemoryConfigStore();
        s.Update(x => x.Telemetry.CollectAnonymousData = collect);
        return s;
    }

    [Fact]
    public void Capture_enqueues_event_with_props_when_enabled()
    {
        var t = new TelemetryClient(Store(true));
        t.Capture("widget_opened", ("widget", "cpu"));

        var batch = t.DrainBatch(10);
        Assert.Single(batch);
        Assert.Equal("widget_opened", batch[0].Name);
        Assert.Contains(batch[0].Properties, p => p.Key == "widget" && (string?)p.Value == "cpu");
    }

    [Fact]
    public void Capture_is_noop_when_opted_out()
    {
        var t = new TelemetryClient(Store(false));
        t.Capture("widget_opened");
        Assert.Empty(t.DrainBatch(10));
    }

    [Fact]
    public void Identify_records_person_props_as_set()
    {
        var t = new TelemetryClient(Store(true));
        t.Identify(("cpu", "Ryzen"), ("ram_amount", 32));

        var batch = t.DrainBatch(10);
        Assert.Single(batch);
        Assert.Equal("$identify", batch[0].Name);
        Assert.NotNull(batch[0].Set);
        Assert.Contains(batch[0].Set!, p => p.Key == "cpu");
    }

    [Fact]
    public void Opting_out_at_runtime_clears_queue_and_stops_capturing()
    {
        var store = Store(true);
        var t = new TelemetryClient(store);
        t.Capture("a");

        store.Update(s => s.Telemetry.CollectAnonymousData = false); // fires OnChanged
        Assert.Empty(t.DrainBatch(10));   // queued event dropped

        t.Capture("b");
        Assert.Empty(t.DrainBatch(10));   // further captures are no-ops
    }

    [Fact]
    public void DrainBatch_honors_the_max_and_drains_the_rest()
    {
        var t = new TelemetryClient(Store(true));
        for (var i = 0; i < 5; i++) t.Capture("e");

        Assert.Equal(2, t.DrainBatch(2).Count);
        Assert.Equal(3, t.DrainBatch(10).Count);
        Assert.Empty(t.DrainBatch(10));
    }
}
