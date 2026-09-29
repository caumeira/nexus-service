using System.Collections.Generic;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests.Store;

public class AppCapabilityGrantsTests
{
    [Fact]
    public void FlattensEveryPrivilegeAndIgnoresNonPrivileges()
    {
        var grants = AppCapabilityGrants.From(new AppManifestCapabilities
        {
            Dispatch = new() { "system.openUrl" },
            NetFetch = new() { "API.Example.com" },
            SensorsRead = new() { "cpu.*" },
            MediaImport = new() { "/tryx/media" },
            AppData = true,
            Audio = true,
            Config = true,
            Code = "worker",
        });

        Assert.Equal(new[]
        {
            "appData", "audio", "dispatch:system.openUrl", "mediaImport:/tryx/media",
            "net.fetch:api.example.com", "sensors.read:cpu.*",
        }, grants);
    }

    [Fact]
    public void MissingListsOnlyUnapprovedGrants()
    {
        Assert.Equal(new[] { "audio" }, AppCapabilityGrants.Missing(new[] { "appData", "audio" }, new List<string> { "appData" }));
        Assert.Equal(new[] { "appData" }, AppCapabilityGrants.Missing(new[] { "appData" }, null));
    }
}
