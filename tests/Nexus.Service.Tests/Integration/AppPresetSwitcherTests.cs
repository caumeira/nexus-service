using System.Collections.Generic;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>Drives AppPresetSwitcher.Tick against the real host so the
/// bindings gate and the activate hand-off are exercised, not just the
/// tracker's decision table.</summary>
[Collection("NexusHost")]
public sealed class AppPresetSwitcherTests : IDisposable
{
    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public string Focused = "";
        public FocusSession? GetCurrentSession() =>
            Focused.Length == 0 ? null : new FocusSession { Id = "1", Name = Focused };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private readonly NexusAppFactory _factory;
    private readonly FakeScreenTime _screenTime = new();

    public AppPresetSwitcherTests()
    {
        _factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ILightingDeviceProvider>();
                s.AddSingleton<ILightingDeviceProvider>(sp =>
                    new StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
            })) as NexusAppFactory ?? new NexusAppFactory();
        // Force the host to build before resolving out of it.
        _ = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    // The test host strips hosted services, so build the switcher from the
    // same container the production registration resolves from.
    private AppPresetSwitcher BuildSwitcher()
    {
        var sp = _factory.Services;
        return new AppPresetSwitcher(
            sp.GetRequiredService<IConfigStore>(),
            _screenTime,
            sp.GetRequiredService<Nexus.Service.Sockets.MultiplexHub>(),
            sp.GetRequiredService<ILightingDeviceProvider>(),
            sp.GetRequiredService<Nexus.Service.Lighting.ILightingProvider>(),
            sp.GetService<Nexus.Service.Lighting.Rgb.RgbBridge>(),
            sp.GetRequiredService<Nexus.Service.Lighting.Smart.SmartLightProvider>(),
            sp.GetRequiredService<Nexus.Service.Lighting.Engine.LightingEngine>());
    }

    private (string bound, string other) SeedPresets(bool withBinding)
    {
        var bound = Guid.NewGuid().ToString("n");
        var other = Guid.NewGuid().ToString("n");
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Clear();
            s.Lighting.LayoutPresets.Add(new LayoutPreset
            {
                Id = bound,
                Name = "Gaming",
                Apps = withBinding
                    ? new List<PresetAppBinding> { new() { Id = "proc:chrome", Name = "chrome", ProcessName = "chrome" } }
                    : null,
            });
            s.Lighting.LayoutPresets.Add(new LayoutPreset { Id = other, Name = "Desk" });
            s.Lighting.ActiveLayoutPresetId = other;
        });
        return (bound, other);
    }

    // Two ticks past the dwell: the first only records the candidate. One
    // switcher instance per test, since the tracker state lives on it.
    private readonly AppPresetSwitcher[] _switcher = new AppPresetSwitcher[1];

    private void TickPastDwell()
    {
        _switcher[0] ??= BuildSwitcher();
        _switcher[0].Tick();
        Thread.Sleep(AppPresetFocusTracker.Dwell + TimeSpan.FromMilliseconds(100));
        _switcher[0].Tick();
    }

    [Fact]
    public void Focusing_a_bound_app_activates_its_preset()
    {
        var (bound, _) = SeedPresets(withBinding: true);
        _screenTime.Focused = "chrome";

        TickPastDwell();

        Assert.Equal(bound, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void Focus_moving_to_an_unbound_app_restores_the_previous_preset()
    {
        var (bound, other) = SeedPresets(withBinding: true);
        _screenTime.Focused = "chrome";
        TickPastDwell();
        Assert.Equal(bound, Store.Load().Lighting.ActiveLayoutPresetId);

        _screenTime.Focused = "notepad";
        TickPastDwell();

        Assert.Equal(other, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void A_preset_with_no_bindings_is_never_activated()
    {
        var (_, other) = SeedPresets(withBinding: false);
        _screenTime.Focused = "chrome";

        TickPastDwell();

        Assert.Equal(other, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void No_focus_reported_changes_nothing()
    {
        var (_, other) = SeedPresets(withBinding: true);
        _screenTime.Focused = "";

        TickPastDwell();

        Assert.Equal(other, Store.Load().Lighting.ActiveLayoutPresetId);
    }
}
