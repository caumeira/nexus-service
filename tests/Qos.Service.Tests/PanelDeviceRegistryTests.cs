using Qos.Service.Models.Panel;
using Qos.Service.Panel;
using Qos.Service.Persistence;

namespace Qos.Service.Tests;

public class PanelDeviceRegistryTests
{
    [Fact]
    public void MigrateLegacyPanelDevices_MovesEntriesFromUiToTopLevel()
    {
        var settings = new QosSettings();
        settings.Ui.PanelDevices = new()
        {
            ["dev1"] = new PanelDeviceRecord { Id = "dev1", DisplayName = "Old Panel" },
            ["dev2"] = new PanelDeviceRecord { Id = "dev2", DisplayName = "Other Panel" },
        };

        Assert.True(PanelDeviceRegistry.HasLegacyPanelDevices(settings));
        PanelDeviceRegistry.MigrateLegacyPanelDevices(settings);

        Assert.Null(settings.Ui.PanelDevices);
        Assert.Equal(2, settings.PanelDevices.Count);
        Assert.Equal("Old Panel", settings.PanelDevices["dev1"].DisplayName);
        Assert.Equal("Other Panel", settings.PanelDevices["dev2"].DisplayName);
    }

    [Fact]
    public void MigrateLegacyPanelDevices_PrefersTopLevelOnIdConflict()
    {
        var settings = new QosSettings();
        settings.PanelDevices["dev1"] = new PanelDeviceRecord { Id = "dev1", DisplayName = "Top-level wins" };
        settings.Ui.PanelDevices = new()
        {
            ["dev1"] = new PanelDeviceRecord { Id = "dev1", DisplayName = "Legacy loses" },
        };

        PanelDeviceRegistry.MigrateLegacyPanelDevices(settings);

        Assert.Equal("Top-level wins", settings.PanelDevices["dev1"].DisplayName);
    }

    [Fact]
    public void MigrateLegacyPanelDevices_IsIdempotent()
    {
        var settings = new QosSettings();
        settings.Ui.PanelDevices = new()
        {
            ["dev1"] = new PanelDeviceRecord { Id = "dev1", DisplayName = "Panel" },
        };

        PanelDeviceRegistry.MigrateLegacyPanelDevices(settings);
        PanelDeviceRegistry.MigrateLegacyPanelDevices(settings);

        Assert.Single(settings.PanelDevices);
        Assert.Null(settings.Ui.PanelDevices);
    }

    [Fact]
    public void MigrateLegacyPanelDevices_NullsOutEmptyLegacyField()
    {
        var settings = new QosSettings();
        settings.Ui.PanelDevices = new(); // empty but present

        Assert.True(PanelDeviceRegistry.HasLegacyPanelDevices(settings));
        PanelDeviceRegistry.MigrateLegacyPanelDevices(settings);

        Assert.Null(settings.Ui.PanelDevices);
        Assert.Empty(settings.PanelDevices);
    }

    [Fact]
    public void MigrateLegacyPanelDevices_HandlesNullTopLevelDictionary()
    {
        // Defensive: a deserialized JSON could carry `"panelDevices": null`
        // and bypass the property default.
        var settings = new QosSettings { PanelDevices = null! };
        settings.Ui.PanelDevices = new()
        {
            ["dev1"] = new PanelDeviceRecord { Id = "dev1", DisplayName = "Panel" },
        };

        PanelDeviceRegistry.MigrateLegacyPanelDevices(settings);

        Assert.NotNull(settings.PanelDevices);
        Assert.Single(settings.PanelDevices);
    }
}
