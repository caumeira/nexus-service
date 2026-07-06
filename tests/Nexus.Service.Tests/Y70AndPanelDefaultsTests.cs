using Nexus.Service.Defaults;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Fresh-install defaults for the panel auto-launch toggle and the Y70 force-
/// orientation flag. Both must default true; existing persisted settings.json
/// files are unaffected since these are new properties that only fall back to
/// the default when absent from the persisted document.
/// </summary>
public class Y70AndPanelDefaultsTests
{
    [Fact]
    public void Panel_AutoLaunch_defaults_true()
    {
        Assert.True(InstallDefaults.Panel.AutoLaunch);
        Assert.True(new NexusSettings().Panel.AutoLaunch);
    }

    [Fact]
    public void Y70_ForceOrientation_defaults_true()
    {
        Assert.True(InstallDefaults.Y70.ForceOrientation);
        Assert.True(new NexusSettings().Y70.ForceOrientation);
    }
}
