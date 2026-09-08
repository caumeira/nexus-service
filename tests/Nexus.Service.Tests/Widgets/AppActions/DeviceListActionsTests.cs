using System;
using System.Collections.Generic;
using System.Text.Json;
using Nexus.Service.Models.Devices;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

/// <summary>Covers the devices.list projection as a pure function; the registered handler resolves DeviceManager from the host container.</summary>
public class DeviceListActionsTests
{
    [Fact]
    public void Projection_keeps_the_five_app_visible_fields()
    {
        var projected = DeviceListActions.Project(new[]
        {
            new DeviceListItem
            {
                Id = "y70",
                Name = "Y70 Touch",
                Category = "case",
                Connected = true,
                FirmwareVersion = "1.2.3",
            },
        });

        var device = Assert.Single(projected.Devices);
        Assert.Equal("y70", device.Id);
        Assert.Equal("Y70 Touch", device.Name);
        Assert.Equal("case", device.Category);
        Assert.True(device.Connected);
        Assert.Equal("1.2.3", device.FirmwareVersion);
    }

    [Fact]
    public void Projection_drops_every_other_field_from_the_wire_shape()
    {
        var projected = DeviceListActions.Project(new[]
        {
            new DeviceListItem
            {
                Id = "np50",
                Name = "NP50",
                Category = "lighting",
                Connected = false,
                FirmwareVersion = "",
                FirmwareType = "np50",
                NexusControlEnabled = false,
                SupportsNexusControl = true,
                Experimental = true,
                Warning = "usb-disconnected",
                ConflictAppId = "icue",
                HasPage = false,
                Bus = "smbus",
            },
        });

        var json = JsonSerializer.Serialize(projected, AppJsonContext.Default.AppDeviceListResponse);
        using var doc = JsonDocument.Parse(json);
        var device = Assert.Single(doc.RootElement.GetProperty("devices").EnumerateArray());

        var names = new List<string>();
        foreach (var property in device.EnumerateObject())
        {
            names.Add(property.Name);
        }
        names.Sort(StringComparer.Ordinal);

        Assert.Equal(new[] { "category", "connected", "firmwareVersion", "id", "name" }, names);
    }

    [Fact]
    public void Disconnected_devices_are_returned_with_their_flag()
    {
        var projected = DeviceListActions.Project(new[]
        {
            new DeviceListItem { Id = "cnvs", Name = "CNVS", Category = "lighting", Connected = false },
            new DeviceListItem { Id = "keeb", Name = "Keeb", Category = "keyboard", Connected = true },
        });

        Assert.Equal(2, projected.Devices.Count);
        Assert.False(projected.Devices[0].Connected);
        Assert.True(projected.Devices[1].Connected);
    }

    [Fact]
    public void Empty_device_list_serializes_as_an_empty_array()
    {
        var projected = DeviceListActions.Project(Array.Empty<DeviceListItem>());

        Assert.Empty(projected.Devices);

        var json = JsonSerializer.Serialize(projected, AppJsonContext.Default.AppDeviceListResponse);
        using var doc = JsonDocument.Parse(json);
        var devices = doc.RootElement.GetProperty("devices");

        Assert.Equal(JsonValueKind.Array, devices.ValueKind);
        Assert.Equal(0, devices.GetArrayLength());
    }

    [Fact]
    public void Action_is_listed_for_registration()
    {
        Assert.Equal(new[] { "devices.list" }, DeviceListActions.AllActions);
    }
}
