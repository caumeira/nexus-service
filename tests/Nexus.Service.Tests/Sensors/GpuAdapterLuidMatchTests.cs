using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class GpuAdapterLuidMatchTests
{
    private static GpuReadout Nvidia(string id = "/gpu-nvidia/0", string name = "NVIDIA GeForce RTX 3070") =>
        new() { Id = id, Name = name, Vendor = "nvidia", Integrated = false };

    private static GpuAdapterLuids.Adapter Adapter(string luid, string desc, uint vendor = 0x10DE, long vramMb = 8018) =>
        new(luid, desc, vendor, vramMb);

    private static Dictionary<string, string> RawNames(params GpuReadout[] gpus)
    {
        var d = new Dictionary<string, string>();
        foreach (var g in gpus) d[g.Id] = g.Name;
        return d;
    }

    // The Y70 case: an indirect-display render clone (0:99003) and the real card
    // (0:66964) both enumerate through DXGI as "NVIDIA GeForce RTX 3070". Only the
    // real card is memory-backed by the graphics kernel, and its LUID is the one
    // the per-process GPU counters carry, so the GPU must bind to it - not the
    // clone DXGI enumerates first.
    [Fact]
    public void Prefers_kernel_backed_luid_over_a_duplicate_named_clone()
    {
        var gpu = Nvidia();
        var adapters = new List<GpuAdapterLuids.Adapter>
        {
            Adapter("0:99003", "NVIDIA GeForce RTX 3070"), // clone, enumerated first
            Adapter("0:66964", "NVIDIA GeForce RTX 3070"), // real card
        };

        GpuAdapterLuids.MatchAdapterLuids(
            new List<GpuReadout> { gpu }, adapters, RawNames(gpu), new HashSet<string> { "0:66964" });

        Assert.Equal("0:66964", gpu.AdapterLuid);
    }

    // Two identical, both-backed physical GPUs bind to distinct LUIDs in
    // enumeration order - stable across polls, not swapping by live usage.
    [Fact]
    public void Two_backed_duplicates_bind_to_distinct_luids_in_order()
    {
        var g0 = Nvidia("/gpu-nvidia/0");
        var g1 = Nvidia("/gpu-nvidia/1");
        var adapters = new List<GpuAdapterLuids.Adapter>
        {
            Adapter("0:11111", "NVIDIA GeForce RTX 3070"),
            Adapter("0:22222", "NVIDIA GeForce RTX 3070"),
        };

        GpuAdapterLuids.MatchAdapterLuids(
            new List<GpuReadout> { g0, g1 }, adapters, RawNames(g0, g1),
            new HashSet<string> { "0:11111", "0:22222" });

        Assert.Equal("0:11111", g0.AdapterLuid);
        Assert.Equal("0:22222", g1.AdapterLuid);
    }

    // Without kernel-LUID info (single adapter, or the counter was unavailable),
    // the first name match wins, exactly as before.
    [Fact]
    public void Falls_back_to_first_match_when_no_kernel_luid_info()
    {
        var gpu = Nvidia();
        var adapters = new List<GpuAdapterLuids.Adapter>
        {
            Adapter("0:99003", "NVIDIA GeForce RTX 3070"),
            Adapter("0:66964", "NVIDIA GeForce RTX 3070"),
        };

        GpuAdapterLuids.MatchAdapterLuids(
            new List<GpuReadout> { gpu }, adapters, RawNames(gpu), new HashSet<string>());

        Assert.Equal("0:99003", gpu.AdapterLuid);
    }

    [Fact]
    public void Matches_single_adapter_by_name()
    {
        var gpu = Nvidia();
        var adapters = new List<GpuAdapterLuids.Adapter> { Adapter("0:66964", "NVIDIA GeForce RTX 3070") };

        GpuAdapterLuids.MatchAdapterLuids(
            new List<GpuReadout> { gpu }, adapters, RawNames(gpu), new HashSet<string>());

        Assert.Equal("0:66964", gpu.AdapterLuid);
    }

    // A GPU whose display name was Astral/AIB-enriched still matches on its raw
    // LHM name; when no description matches, the vendor+discrete class binds the
    // leftover discrete GPU.
    [Fact]
    public void Vendor_class_fallback_binds_discrete_gpu_when_name_differs()
    {
        var gpu = new GpuReadout { Id = "/gpu-nvidia/0", Name = "Gigabyte GeForce RTX 3070", Vendor = "nvidia" };
        var rawNames = new Dictionary<string, string> { [gpu.Id] = "NVIDIA GeForce RTX 3070" };
        var adapters = new List<GpuAdapterLuids.Adapter>
        {
            Adapter("0:66964", "NVIDIA GeForce RTX 3070 Laptop GPU"),
        };

        GpuAdapterLuids.MatchAdapterLuids(
            new List<GpuReadout> { gpu }, adapters, rawNames, new HashSet<string>());

        Assert.Equal("0:66964", gpu.AdapterLuid);
    }

    [Theory]
    [InlineData("pid_5040_luid_0x00000000_0x00010594_phys_0_eng_0_engtype_3d", "0:66964")]
    [InlineData("luid_0x00000000_0x000182bb_phys_0", "0:99003")]
    [InlineData("luid_0x00000001_0x0000000a_phys_0", "1:10")]
    [InlineData("pid_10_engtype_3d", "")]
    public void ParseInstanceLuid_normalizes_to_dxgi_form(string instance, string expected)
    {
        Assert.Equal(expected, GpuAdapterLuids.ParseInstanceLuid(instance));
    }
}
