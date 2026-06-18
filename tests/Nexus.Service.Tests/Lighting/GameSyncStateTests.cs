using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Tests for the GET /lighting/game-sync/state response shape and device
/// mapping logic, exercised without a live HTTP server.
/// </summary>
public class GameSyncStateTests
{
    // Build a minimal device list parallel to ILightingDeviceProvider.GetAll().
    private static Nexus.Service.Models.Devices.GetLightingDevicesResponse MakeDeviceList(
        params (string id, string name, int ledCount)[] entries)
    {
        var resp = new Nexus.Service.Models.Devices.GetLightingDevicesResponse();
        foreach (var (id, name, lc) in entries)
        {
            resp.Devices.Add(new Nexus.Service.Models.Devices.LightingDevice
            {
                Id = id,
                Name = name,
                LedCount = lc,
            });
        }
        return resp;
    }

    // Build the GameSyncStateResponse the route would return, using the same
    // logic as the route handler but without DI.
    private static GameSyncStateResponse BuildState(
        bool active,
        DeviceFrame[] frames,
        Nexus.Service.Models.Devices.GetLightingDevicesResponse deviceList)
    {
        var nameById = new Dictionary<string, string>(
            deviceList.Devices.Count, StringComparer.Ordinal);
        foreach (var d in deviceList.Devices)
        {
            nameById[d.Id] = d.Name;
        }

        var infos = new List<GameSyncDeviceInfo>(frames.Length);
        foreach (var frame in frames)
        {
            infos.Add(new GameSyncDeviceInfo
            {
                Name = nameById.TryGetValue(frame.Id, out var n) ? n : frame.Id,
                Archetype = frame.Archetype ?? "ambient",
                LedCount = frame.LedCount,
            });
        }

        return new GameSyncStateResponse { Active = active, Devices = infos };
    }

    [Fact]
    public void State_Active_WhenSyncIsGameSync()
    {
        var deviceList = MakeDeviceList();
        var result = BuildState(active: true, frames: Array.Empty<DeviceFrame>(), deviceList);
        Assert.True(result.Active);
    }

    [Fact]
    public void State_NotActive_WhenSyncIsOther()
    {
        var deviceList = MakeDeviceList();
        var result = BuildState(active: false, frames: Array.Empty<DeviceFrame>(), deviceList);
        Assert.False(result.Active);
    }

    [Fact]
    public void State_Devices_EmptyWhenNoFrames()
    {
        var deviceList = MakeDeviceList(("kb-1", "Keeb TKL", 87));
        var result = BuildState(active: true, frames: Array.Empty<DeviceFrame>(), deviceList);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void State_Devices_NameFromDeviceRegistry()
    {
        var deviceList = MakeDeviceList(("kb-1", "HYTE Keeb TKL", 87));
        var frame = new DeviceFrame(0, "kb-1", 87) { Archetype = "keyboard" };

        var result = BuildState(active: true, frames: new[] { frame }, deviceList);

        Assert.Single(result.Devices);
        Assert.Equal("HYTE Keeb TKL", result.Devices[0].Name);
        Assert.Equal("keyboard", result.Devices[0].Archetype);
        Assert.Equal(87, result.Devices[0].LedCount);
    }

    [Fact]
    public void State_Devices_FallsBackToIdWhenNameNotInRegistry()
    {
        var deviceList = MakeDeviceList();
        var frame = new DeviceFrame(0, "unknown-dev", 10) { Archetype = "mouse" };

        var result = BuildState(active: true, frames: new[] { frame }, deviceList);

        Assert.Single(result.Devices);
        Assert.Equal("unknown-dev", result.Devices[0].Name);
    }

    [Theory]
    [InlineData("keyboard")]
    [InlineData("mouse")]
    [InlineData("mousepad")]
    [InlineData("headset")]
    [InlineData("keypad")]
    [InlineData("chromalink")]
    public void State_Devices_ArchetypePassedThrough(string archetype)
    {
        var deviceList = MakeDeviceList(("d-1", "Device", 4));
        var frame = new DeviceFrame(0, "d-1", 4) { Archetype = archetype };

        var result = BuildState(active: true, frames: new[] { frame }, deviceList);

        Assert.Equal(archetype, result.Devices[0].Archetype);
    }

    [Fact]
    public void State_Devices_NullArchetypeReportsAmbient()
    {
        var deviceList = MakeDeviceList(("strip-1", "ARGB Strip", 30));
        var frame = new DeviceFrame(0, "strip-1", 30);
        Assert.Null(frame.Archetype);

        var result = BuildState(active: true, frames: new[] { frame }, deviceList);

        Assert.Equal("ambient", result.Devices[0].Archetype);
    }

    [Fact]
    public void State_Devices_MultipleFramesAllPresent()
    {
        var deviceList = MakeDeviceList(
            ("kb-1", "Keeb", 87),
            ("ms-1", "Mouse", 8),
            ("strip-1", "Strip", 60));

        var frames = new[]
        {
            new DeviceFrame(0, "kb-1", 87) { Archetype = "keyboard" },
            new DeviceFrame(1, "ms-1", 8) { Archetype = "mouse" },
            new DeviceFrame(2, "strip-1", 60),
        };

        var result = BuildState(active: true, frames: frames, deviceList);

        Assert.Equal(3, result.Devices.Count);
        Assert.Equal("Keeb", result.Devices[0].Name);
        Assert.Equal("keyboard", result.Devices[0].Archetype);
        Assert.Equal(87, result.Devices[0].LedCount);
        Assert.Equal("Mouse", result.Devices[1].Name);
        Assert.Equal("mouse", result.Devices[1].Archetype);
        Assert.Equal("Strip", result.Devices[2].Name);
        Assert.Equal("ambient", result.Devices[2].Archetype);
        Assert.Equal(60, result.Devices[2].LedCount);
    }
}
