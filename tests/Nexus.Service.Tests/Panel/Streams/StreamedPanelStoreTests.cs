using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Panel.Streams;
using Xunit;

namespace Nexus.Service.Tests.Panel.Streams;

public class StreamedPanelStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public StreamedPanelStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-streamed-panel-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "streamed-panels.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        var store = new StreamedPanelStore(_path);
        var result = store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_returns_empty_when_file_empty()
    {
        File.WriteAllText(_path, string.Empty);
        var store = new StreamedPanelStore(_path);
        var result = store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Load_returns_empty_when_file_corrupt()
    {
        File.WriteAllText(_path, "{not valid json");
        var store = new StreamedPanelStore(_path);
        var result = store.Load();
        Assert.Empty(result);
    }

    [Fact]
    public void Save_then_Load_roundtrips_records()
    {
        var store = new StreamedPanelStore(_path);
        var input = new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal)
        {
            ["d211_demo128_nand"] = new StreamedPanelRecord
            {
                PanelDeviceId = "QRuzc_tZpgw6",
                ProfileKind = "d213-q60",
                Fps = 30,
                BitrateKbps = 4000,
            },
            ["d211_other_nand"] = new StreamedPanelRecord
            {
                PanelDeviceId = "_JrD3WxrMNtf",
            },
        };
        store.Save(input);

        var roundtrip = store.Load();
        Assert.Equal(2, roundtrip.Count);
        Assert.Equal("QRuzc_tZpgw6", roundtrip["d211_demo128_nand"].PanelDeviceId);
        Assert.Equal("d213-q60", roundtrip["d211_demo128_nand"].ProfileKind);
        Assert.Equal(30, roundtrip["d211_demo128_nand"].Fps);
        Assert.Equal(4000, roundtrip["d211_demo128_nand"].BitrateKbps);
        Assert.Equal("_JrD3WxrMNtf", roundtrip["d211_other_nand"].PanelDeviceId);
        Assert.Null(roundtrip["d211_other_nand"].ProfileKind);
    }

    [Fact]
    public void Save_overwrites_previous_state()
    {
        var store = new StreamedPanelStore(_path);
        store.Save(new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal)
        {
            ["A"] = new StreamedPanelRecord { PanelDeviceId = "device-a" },
        });
        store.Save(new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal)
        {
            ["B"] = new StreamedPanelRecord { PanelDeviceId = "device-b" },
        });

        var roundtrip = store.Load();
        Assert.Single(roundtrip);
        Assert.Contains("B", roundtrip.Keys);
        Assert.DoesNotContain("A", roundtrip.Keys);
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        var nestedDir = Path.Combine(_tempDir, "deep", "nested", "dir");
        var nestedPath = Path.Combine(nestedDir, "streamed-panels.json");
        var store = new StreamedPanelStore(nestedPath);
        store.Save(new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal)
        {
            ["X"] = new StreamedPanelRecord { PanelDeviceId = "device-x" },
        });
        Assert.True(File.Exists(nestedPath));
    }
}
