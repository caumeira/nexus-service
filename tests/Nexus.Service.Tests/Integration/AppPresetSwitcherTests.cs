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
public sealed class AppPresetSwitcherTests : IClassFixture<StubDeviceHostFactory>, IDisposable
{
    private sealed class FakeScreenTime : IScreenTimeProvider
    {
        public string Focused = "";
        public event Action? FocusChanged;
        public void Focus(string app) { Focused = app; FocusChanged?.Invoke(); }
        public FocusSession? GetCurrentSession() =>
            Focused.Length == 0 ? null : new FocusSession { Id = "1", Name = Focused };
        public IReadOnlyList<AppUsage> GetTodayUsage() => Array.Empty<AppUsage>();
    }

    private readonly StubDeviceHostFactory _factory;
    private readonly FakeScreenTime _screenTime = new();

    public AppPresetSwitcherTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        // Force the host to build before resolving out of it.
        _ = _factory.CreateClient();
    }

    public void Dispose() => _switcher?.Dispose();

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

    // One started switcher per test: it subscribes to the provider's
    // FocusChanged, so the tests drive the real wiring rather than Tick.
    private AppPresetSwitcher? _switcher;

    private void Focus(string app)
    {
        if (_switcher is null)
        {
            _switcher = BuildSwitcher();
            _switcher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        _screenTime.Focus(app);
        // The dwell timer re-evaluates once the candidate has settled.
        Thread.Sleep(AppPresetFocusTracker.Dwell + TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public void Focusing_a_bound_app_activates_its_preset()
    {
        var (bound, _) = SeedPresets(withBinding: true);

        Focus("chrome");

        Assert.Equal(bound, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void Focus_moving_to_an_unbound_app_restores_the_previous_preset()
    {
        var (bound, other) = SeedPresets(withBinding: true);
        Focus("chrome");
        Assert.Equal(bound, Store.Load().Lighting.ActiveLayoutPresetId);

        Focus("notepad");

        Assert.Equal(other, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void A_preset_with_no_bindings_is_never_activated()
    {
        var (_, other) = SeedPresets(withBinding: false);

        Focus("chrome");

        Assert.Equal(other, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void No_focus_reported_changes_nothing()
    {
        var (_, other) = SeedPresets(withBinding: true);

        Focus("");

        Assert.Equal(other, Store.Load().Lighting.ActiveLayoutPresetId);
    }

    [Fact]
    public void Saving_a_binding_while_its_app_is_focused_activates_without_a_refocus()
    {
        var (bound, _) = SeedPresets(withBinding: false);
        Focus("chrome");

        // No focus event follows a settings write; the store change is what
        // has to wake the switcher.
        Store.Update(s =>
        {
            s.Lighting.LayoutPresets.Find(p => p.Id == bound)!.Apps =
                new List<PresetAppBinding> { new() { Id = "proc:chrome", Name = "chrome", ProcessName = "chrome" } };
        });
        Thread.Sleep(AppPresetFocusTracker.Dwell + TimeSpan.FromMilliseconds(400));

        Assert.Equal(bound, Store.Load().Lighting.ActiveLayoutPresetId);
    }
}
