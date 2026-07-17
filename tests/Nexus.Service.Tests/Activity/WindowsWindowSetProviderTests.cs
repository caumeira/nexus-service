#if WINDOWS
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Activity;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class WindowsWindowSetProviderTests
{
    private static HelperEnvelope SnapshotEnvelope(params int[] pids)
    {
        var payload = new WindowSetSnapshotPayload { Pids = new List<int>(pids) };
        return new HelperEnvelope
        {
            Type = "windowSet.snapshot",
            Payload = JsonSerializer.SerializeToElement(payload, AppJsonContext.Default.WindowSetSnapshotPayload),
        };
    }

    [Fact]
    public void IsWindowed_ReflectsTheLatestIngestedSnapshot()
    {
        var provider = new WindowsWindowSetProvider(new HelperRegistry());

        provider.IngestEnvelopeForTest(SnapshotEnvelope(4242));

        Assert.True(provider.IsWindowed(4242));
        Assert.False(provider.IsWindowed(1));
    }

    [Fact]
    public void IsWindowed_ReturnsFalse_AfterADisconnectClearsAWarmSnapshot()
    {
        // A crashed helper never sends a snapshot update, so the only signal
        // that the last set is stale is the registry's Disconnected event.
        var provider = new WindowsWindowSetProvider(new HelperRegistry());
        provider.IngestEnvelopeForTest(SnapshotEnvelope(4242));
        Assert.True(provider.IsWindowed(4242));

        provider.DisconnectForTest();

        Assert.False(provider.IsWindowed(4242));
    }
}
#endif
