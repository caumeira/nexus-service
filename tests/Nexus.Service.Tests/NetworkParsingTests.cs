using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Tests;

/// <summary>
/// Tests for network provider parsing logic.
/// The MacNetworkProvider parses nettop output and merges by process name.
/// These tests validate the parsing and merging algorithms using sample data.
/// </summary>
public class NetworkParsingTests
{
    [Fact]
    public void ParseNettopLine_ExtractsFields()
    {
        var line = "Chrome.12345,1024,512";
        var parts = line.Split(',');

        Assert.Equal("Chrome.12345", parts[0].Trim());
        Assert.True(long.TryParse(parts[1].Trim(), out var bytesIn));
        Assert.True(long.TryParse(parts[2].Trim(), out var bytesOut));
        Assert.Equal(1024, bytesIn);
        Assert.Equal(512, bytesOut);
    }

    [Fact]
    public void StripPidSuffix_RemovesPidFromProcessName()
    {
        Assert.Equal("Chrome", StripPid("Chrome.12345"));
        Assert.Equal("Safari", StripPid("Safari.67890"));
        Assert.Equal("nexus-service", StripPid("nexus-service.999"));
    }

    [Fact]
    public void StripPidSuffix_PreservesNamesWithoutPid()
    {
        Assert.Equal("kernel_task", StripPid("kernel_task"));
        Assert.Equal("mDNSResponder", StripPid("mDNSResponder"));
    }

    [Fact]
    public void StripPidSuffix_PreservesNamesWithNonNumericSuffix()
    {
        Assert.Equal("com.apple.WebKit.Networking", StripPid("com.apple.WebKit.Networking"));
        Assert.Equal("node.js", StripPid("node.js"));
    }

    [Fact]
    public void MergeByName_CombinesBytesForSameProcess()
    {
        var entries = new[]
        {
            ("Chrome", 1000L, 500L),
            ("Chrome", 2000L, 300L),
            ("Safari", 100L, 50L),
        };

        var merged = MergeByName(entries);

        Assert.Equal(2, merged.Count);
        var chrome = merged.First(e => e.Name == "Chrome");
        Assert.Equal(3000, chrome.BytesIn);
        Assert.Equal(800, chrome.BytesOut);
    }

    [Fact]
    public void MergeByName_SortsByTotalDescending()
    {
        var entries = new[]
        {
            ("Slack", 100L, 50L),
            ("Chrome", 5000L, 3000L),
            ("Spotify", 2000L, 100L),
        };

        var merged = MergeByName(entries);

        Assert.Equal("Chrome", merged[0].Name);  // 8000 total
        Assert.Equal("Spotify", merged[1].Name);  // 2100 total
        Assert.Equal("Slack", merged[2].Name);     // 150 total
    }

    [Fact]
    public void MergeByName_FiltersZeroTraffic()
    {
        var entries = new[]
        {
            ("Chrome", 1000L, 500L),
            ("IdleProcess", 0L, 0L),
        };

        var merged = MergeByName(entries);

        Assert.Single(merged);
        Assert.Equal("Chrome", merged[0].Name);
    }

    [Fact]
    public void ParseFullNettopOutput_HandlesMultipleLines()
    {
        var output = "Chrome.1234,10240,5120\nChrome.5678,2048,1024\nSafari.9999,512,256\n";
        var result = ParseNettopOutput(output);

        Assert.Equal(2, result.Count);
        var chrome = result.First(r => r.Name == "Chrome");
        Assert.Equal(12288, chrome.BytesIn);   // 10240 + 2048
        Assert.Equal(6144, chrome.BytesOut);    // 5120 + 1024
    }

    [Fact]
    public void ParseFullNettopOutput_SkipsMalformedLines()
    {
        var output = "Chrome.1234,10240,5120\nbadline\n,,,\nSafari.9999,512,256\n";
        var result = ParseNettopOutput(output);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseFullNettopOutput_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ParseNettopOutput(""));
        Assert.Empty(ParseNettopOutput("   \n  \n"));
    }

    // --- Helpers matching MacNetworkProvider.Sample() logic ---

    private static string StripPid(string rawName)
    {
        var dotIdx = rawName.LastIndexOf('.');
        if (dotIdx > 0 && int.TryParse(rawName.AsSpan(dotIdx + 1), out _))
            return rawName.Substring(0, dotIdx);
        return rawName;
    }

    private static List<NetworkProcessInfo> MergeByName(IEnumerable<(string name, long bytesIn, long bytesOut)> entries)
    {
        var merged = new Dictionary<string, (long bytesIn, long bytesOut)>();
        foreach (var (name, bytesIn, bytesOut) in entries)
        {
            if (merged.TryGetValue(name, out var prev))
                merged[name] = (prev.bytesIn + bytesIn, prev.bytesOut + bytesOut);
            else
                merged[name] = (bytesIn, bytesOut);
        }

        return merged
            .Where(kv => kv.Value.bytesIn + kv.Value.bytesOut > 0)
            .OrderByDescending(kv => kv.Value.bytesIn + kv.Value.bytesOut)
            .Select(kv => new NetworkProcessInfo
            {
                Name = kv.Key,
                BytesIn = kv.Value.bytesIn,
                BytesOut = kv.Value.bytesOut,
            })
            .ToList();
    }

    // --- NetstatParser (Windows) ---

    [Theory]
    [InlineData("127.0.0.1:9400")]
    [InlineData("127.255.255.255:80")]
    [InlineData("0.0.0.0:0")]
    [InlineData("[::1]:443")]
    [InlineData("[::]:0")]
    [InlineData("*:*")]
    [InlineData("[::ffff:127.0.0.1]:1234")]
    public void IsLoopbackOrUnspecified_True(string foreignAddress)
    {
        Assert.True(NetstatParser.IsLoopbackOrUnspecified(foreignAddress));
    }

    [Theory]
    [InlineData("142.250.80.46:443")]      // public IPv4
    [InlineData("192.168.1.235:9400")]     // LAN (still real wire traffic)
    [InlineData("8.8.8.8:53")]
    [InlineData("[2606:4700::1]:443")]     // public IPv6
    [InlineData("[fe80::1]:5353")]         // IPv6 link-local
    public void IsLoopbackOrUnspecified_False(string foreignAddress)
    {
        Assert.False(NetstatParser.IsLoopbackOrUnspecified(foreignAddress));
    }

    [Fact]
    public void ParseInternetActivePids_ExcludesLoopbackOnlyPids()
    {
        // nexus-service (PID 9876) only talks to localhost.
        // Edge browser (PID 4242) has both a loopback handshake AND public traffic.
        var output = """
        Active Connections

          Proto  Local Address          Foreign Address        State           PID
          TCP    127.0.0.1:9400         127.0.0.1:50123        ESTABLISHED     9876
          TCP    127.0.0.1:9400         127.0.0.1:50124        ESTABLISHED     9876
          TCP    127.0.0.1:50123        127.0.0.1:9400         ESTABLISHED     4242
          TCP    192.168.1.35:50500     142.250.80.46:443      ESTABLISHED     4242
          TCP    0.0.0.0:9400           0.0.0.0:0              LISTENING       9876
          UDP    0.0.0.0:5353           *:*                                    1111
        """;

        var pids = NetstatParser.ParseInternetActivePids(output);

        Assert.Contains(4242, pids);     // Edge has at least one real peer
        Assert.DoesNotContain(9876, pids); // nexus-service is loopback-only
        Assert.DoesNotContain(1111, pids); // mDNS listener has no real peer
    }

    [Fact]
    public void ParseInternetActivePids_HandlesIpv6Peers()
    {
        var output = """
          TCP    [::1]:9400             [::1]:50123            ESTABLISHED     9876
          TCP    [2001:db8::1]:50500    [2606:4700::1]:443     ESTABLISHED     4242
        """;

        var pids = NetstatParser.ParseInternetActivePids(output);

        Assert.Contains(4242, pids);
        Assert.DoesNotContain(9876, pids);
    }

    [Fact]
    public void ParseInternetActivePids_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(NetstatParser.ParseInternetActivePids(""));
        Assert.Empty(NetstatParser.ParseInternetActivePids("Active Connections\n"));
    }

    [Fact]
    public void ParseInternetActivePids_IgnoresKernelPid()
    {
        var output = """
          TCP    192.168.1.35:445       142.250.80.46:443      ESTABLISHED     0
          TCP    192.168.1.35:50500     142.250.80.46:443      ESTABLISHED     4242
        """;

        var pids = NetstatParser.ParseInternetActivePids(output);

        Assert.Single(pids);
        Assert.Contains(4242, pids);
    }

    private static List<NetworkProcessInfo> ParseNettopOutput(string output)
    {
        var merged = new Dictionary<string, (long bytesIn, long bytesOut)>();

        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;

            var rawName = parts[0].Trim();
            var name = StripPid(rawName);
            if (string.IsNullOrEmpty(name)) continue;
            if (!long.TryParse(parts[1].Trim(), out var bytesIn)) continue;
            if (!long.TryParse(parts[2].Trim(), out var bytesOut)) continue;

            if (merged.TryGetValue(name, out var prev))
                merged[name] = (prev.bytesIn + bytesIn, prev.bytesOut + bytesOut);
            else
                merged[name] = (bytesIn, bytesOut);
        }

        return merged
            .Where(kv => kv.Value.bytesIn + kv.Value.bytesOut > 0)
            .OrderByDescending(kv => kv.Value.bytesIn + kv.Value.bytesOut)
            .Select(kv => new NetworkProcessInfo
            {
                Name = kv.Key,
                BytesIn = kv.Value.bytesIn,
                BytesOut = kv.Value.bytesOut,
            })
            .ToList();
    }
}
