using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// GetSettings must round-trip every field the panel renders - the settings
/// tab and the rotary editor both restore from this response on page load,
/// so a field missing here silently resets in the UI after every reload.
/// </summary>
public class KeebProviderSettingsTests
{
    [Fact]
    public void StubProvider_GetSettings_RoundTripsRotaryAndPassiveLighting()
    {
        var store = new InMemoryConfigStore();
        var provider = new StubKeebProvider(store);

        provider.SetRotary(new SetRotaryWheelsBody { Left = "ScrollY", Right = "Scale" });
        provider.SetRotarySensitivity("Turbo");
        provider.SetPassiveLighting(new SetPassiveLightingBody
        {
            KeyReactive = true,
            KeyReactiveMask = true,
            KeyReactiveMode = "Ripple",
            KeyReactiveColor = new() { R = 1, G = 2, B = 3, A = 4 },
        });
        provider.SetGameMode(new SetGameModeBody { AltF4 = true, WindowsKey = true });

        var s = provider.GetSettings();

        Assert.Equal("ScrollY", s.RotaryLeft);
        Assert.Equal("Scale", s.RotaryRight);
        Assert.Equal("Turbo", s.RotarySensitivity);
        Assert.True(s.KeyReactive);
        Assert.True(s.KeyReactiveMask);
        Assert.Equal("Ripple", s.KeyReactiveMode);
        Assert.Equal(3, s.KeyReactiveColor.B);
        Assert.True(s.AltF4Disabled);
        Assert.True(s.WindowsKeyDisabled);
        Assert.False(s.AltTabDisabled);
    }

    [Fact]
    public void StubProvider_GetSettings_DefaultsBeforeAnyWrite()
    {
        var provider = new StubKeebProvider(new InMemoryConfigStore());

        var s = provider.GetSettings();

        // Install defaults seed the persisted state; the response must carry
        // them rather than empty strings.
        Assert.Equal(Defaults.InstallDefaults.Keeb.RotaryLeft, s.RotaryLeft);
        Assert.Equal(Defaults.InstallDefaults.Keeb.RotaryRight, s.RotaryRight);
        Assert.Equal(Defaults.InstallDefaults.Keeb.RotarySensitivity, s.RotarySensitivity);
    }
}
