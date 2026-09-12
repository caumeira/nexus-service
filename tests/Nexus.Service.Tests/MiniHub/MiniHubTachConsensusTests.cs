using System;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Xunit;

namespace Nexus.Service.Tests.MiniHub;

public class MiniHubTachConsensusTests
{
    // Raw period bytes captured on the Y70 hub (fw 1.0.1.1) under the LED
    // stream at 500 ms; every 4th sample is what the 2 s heartbeat sees.
    private const string Port1Software60 = "02 02 02 02 02 02 02 02 02 02 02 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 04 22 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 8D 45 45 45 45 60 60 60";
    private const string Port2Software30 = "E3 08 08 28 28 2E C0 3B 09 F5 04 04 04 09 09 09 05 DE 09 0C 3F CD 91 BF DA C4 DB C1 D7 CE CE AB BA BA BA BA BA 6C 0A 0A C5 03 9C 9C 07 03 03 03 03 03 03 7E 5D 69 DE 05 B7 03 17 9A AB E2 B1 7D B2 B2 B2 B2 B2 1C 1C";
    private static readonly string Port2Software100 = string.Concat(Enumerable.Repeat("AC ", 70));
    private const string Port1Motherboard = "C7 C7 C7 C7 C7 C7 C7 C7 C7 C7 C7 C7 C7 C7 C7 0A 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 05 C6 C6 47 BF BF BF BF BF BF 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A 0A";
    private const string Port2Motherboard = "2D 2D 5D 5D 5D 7E 7E 7E 7E 7E 7E 4E 4E 4E 4E 01 01 AB AB AB AB 03 03 CE 03 03 03 41 41 41 41 41 41 41 41 41 01 01 81 81 81 45 45 45 45 62 62 62 62 62 03 03 03 03 03 EF 1E 1E 1E BA BA BA 01 01 CA 01 29 AC AC AC AC";

    [Fact]
    public void Port1_at_60_percent_agrees_on_the_dominant_reading_and_drops_the_glitches()
    {
        var c = Feed(Port1Software60);
        // 0x8D = 141 → 60000 / (141 * 0.4) = 1063 RPM, the value held for 15 s.
        Assert.Equal(1063, c.Evaluate());
    }

    [Fact]
    public void Port2_chain_at_30_percent_never_agrees()
    {
        var c = Feed(Port2Software30);
        Assert.Null(c.Evaluate());
    }

    [Fact]
    public void Port2_chain_at_100_percent_is_stable_and_publishes()
    {
        var c = Feed(Port2Software100);
        Assert.Equal(872, c.Evaluate());
    }

    [Fact]
    public void Motherboard_mode_keeps_port1_where_it_agrees_and_hides_port2()
    {
        // 0xC7 = 199 → 753 RPM held long enough; the 0x05/0x0A junk decodes
        // past 4000 RPM and never counts.
        Assert.Equal(753, Feed(Port1Motherboard).Evaluate());
        Assert.Null(Feed(Port2Motherboard).Evaluate());
    }

    [Fact]
    public void Fewer_than_MinAgreeing_matching_samples_publish_nothing()
    {
        var c = new MiniHubTachConsensus();
        for (var i = 0; i < MiniHubTachConsensus.MinAgreeing - 1; i++) c.Add(1200);
        c.Add(15000);
        c.Add(590);
        Assert.Null(c.Evaluate());
        c.Add(1210);
        Assert.Equal(1200, c.Evaluate());
        c.Add(1300);
        Assert.Equal(1200, c.Evaluate());
    }

    [Fact]
    public void Implausible_values_never_form_a_cluster()
    {
        var c = new MiniHubTachConsensus();
        for (var i = 0; i < MiniHubTachConsensus.WindowSize; i++) c.Add(150000);
        Assert.Null(c.Evaluate());
    }

    [Fact]
    public void Window_evicts_the_oldest_samples()
    {
        var c = new MiniHubTachConsensus();
        for (var i = 0; i < MiniHubTachConsensus.MinAgreeing; i++) c.Add(900);
        Assert.Equal(900, c.Evaluate());
        for (var i = 0; i < MiniHubTachConsensus.WindowSize; i++) c.Add(30000);
        Assert.Null(c.Evaluate());
    }

    [Fact]
    public void Reset_clears_the_window()
    {
        var c = new MiniHubTachConsensus();
        for (var i = 0; i < MiniHubTachConsensus.WindowSize; i++) c.Add(1000);
        c.Reset();
        Assert.Null(c.Evaluate());
    }

    private static MiniHubTachConsensus Feed(string hexBytes)
    {
        var c = new MiniHubTachConsensus();
        var bytes = hexBytes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => Convert.ToByte(h, 16)).ToArray();
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var reply = new byte[] { 0xFF, 0xDD, 0x06, 0x01, 0x00, bytes[i], 0x02, 0x00, bytes[i] };
            Assert.True(MiniHubProtocol.TryParseFanSpeeds(reply, out var rpm, out _));
            c.Add(rpm);
        }
        return c;
    }
}
