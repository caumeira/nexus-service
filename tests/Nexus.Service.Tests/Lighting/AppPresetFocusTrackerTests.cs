using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting;

public class AppPresetFocusTrackerTests
{
    private static readonly long DwellMs = (long)AppPresetFocusTracker.Dwell.TotalMilliseconds;

    private static LayoutPreset Preset(string id, params PresetAppBinding[] apps) =>
        new() { Id = id, Name = id, Apps = new List<PresetAppBinding>(apps) };

    private static PresetAppBinding Bind(string process, string name = "") =>
        new() { Id = "app-" + process, Name = name.Length == 0 ? process : name, ProcessName = process };

    [Fact]
    public void Activates_bound_preset_once_the_app_holds_focus()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")) };

        Assert.Null(tracker.Decide("chrome", null, presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", null, presets, DwellMs));
    }

    [Fact]
    public void Does_not_activate_before_the_dwell_elapses()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")) };

        Assert.Null(tracker.Decide("chrome", null, presets, 0));
        Assert.Null(tracker.Decide("chrome", null, presets, DwellMs - 1));
    }

    [Fact]
    public void Alt_tabbing_through_apps_activates_nothing()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")) };

        Assert.Null(tracker.Decide("chrome", null, presets, 0));
        Assert.Null(tracker.Decide("code", null, presets, 500));
        Assert.Null(tracker.Decide("chrome", null, presets, 1000));
        Assert.Null(tracker.Decide("explorer", null, presets, 1400));
    }

    [Fact]
    public void Restores_the_preset_that_was_active_before_the_switch()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset>
        {
            Preset("gaming", Bind("chrome")),
            Preset("desk"),
        };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        Assert.Null(tracker.Decide("notepad", "gaming", presets, DwellMs + 1));
        Assert.Equal("desk", tracker.Decide("notepad", "gaming", presets, DwellMs * 2 + 1));
    }

    [Fact]
    public void Second_bound_app_switches_directly_and_still_restores_the_original()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset>
        {
            Preset("gaming", Bind("chrome")),
            Preset("work", Bind("code")),
            Preset("desk"),
        };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        Assert.Null(tracker.Decide("code", "gaming", presets, DwellMs + 1));
        Assert.Equal("work", tracker.Decide("code", "gaming", presets, DwellMs * 2 + 1));

        Assert.Null(tracker.Decide("notepad", "work", presets, DwellMs * 2 + 2));
        Assert.Equal("desk", tracker.Decide("notepad", "work", presets, DwellMs * 3 + 2));
    }

    [Fact]
    public void A_manual_pick_during_an_app_switch_cancels_the_restore()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset>
        {
            Preset("gaming", Bind("chrome")),
            Preset("desk"),
            Preset("party"),
        };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        // The user selected "party" while the app-triggered preset was live.
        Assert.Null(tracker.Decide("notepad", "party", presets, DwellMs + 1));
        Assert.Null(tracker.Decide("notepad", "party", presets, DwellMs * 2 + 1));
    }

    [Fact]
    public void No_restore_when_the_original_preset_was_deleted()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")), Preset("desk") };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        presets.RemoveAll(p => p.Id == "desk");
        Assert.Null(tracker.Decide("notepad", "gaming", presets, DwellMs + 1));
        Assert.Null(tracker.Decide("notepad", "gaming", presets, DwellMs * 2 + 1));
    }

    [Fact]
    public void Already_active_bound_preset_is_not_reactivated()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")) };

        Assert.Null(tracker.Decide("chrome", "gaming", presets, 0));
        Assert.Null(tracker.Decide("chrome", "gaming", presets, DwellMs));
    }

    [Fact]
    public void Empty_focus_holds_the_current_state()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")), Preset("desk") };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        // Helper disconnected: no focus reported must not read as "unbound app".
        Assert.Null(tracker.Decide("", "gaming", presets, DwellMs * 2 + 1));
        Assert.Null(tracker.Decide("", "gaming", presets, DwellMs * 3 + 1));
    }

    [Fact]
    public void Unbound_app_with_no_prior_switch_changes_nothing()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")), Preset("desk") };

        Assert.Null(tracker.Decide("notepad", "desk", presets, 0));
        Assert.Null(tracker.Decide("notepad", "desk", presets, DwellMs));
    }

    [Fact]
    public void Unresolved_binding_falls_back_to_the_display_name()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset>
        {
            Preset("gaming", new PresetAppBinding { Id = "u", Name = "Google Chrome", ProcessName = "" }),
        };

        Assert.Null(tracker.Decide("chrome", null, presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", null, presets, DwellMs));
    }

    [Fact]
    public void Binding_an_app_that_already_holds_focus_takes_effect_without_a_refocus()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming"), Preset("desk") };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Null(tracker.Decide("chrome", "desk", presets, DwellMs));

        presets[0] = Preset("gaming", Bind("chrome"));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs + 1));
    }

    [Fact]
    public void A_manual_pick_while_the_bound_app_stays_focused_is_not_overridden()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset>
        {
            Preset("gaming", Bind("chrome")),
            Preset("desk"),
            Preset("party"),
        };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        // The user picked "party" and never left the app.
        Assert.Null(tracker.Decide("chrome", "party", presets, DwellMs + 1));
        Assert.Null(tracker.Decide("chrome", "party", presets, DwellMs * 2));
    }

    [Fact]
    public void Two_apps_sharing_one_preset_do_not_re_activate_it()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset>
        {
            Preset("gaming", Bind("chrome"), Bind("code")),
            Preset("desk"),
        };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        Assert.Null(tracker.Decide("code", "gaming", presets, DwellMs + 1));
        Assert.Null(tracker.Decide("code", "gaming", presets, DwellMs * 2 + 1));

        // The restore target is still the preset from before the first switch.
        Assert.Null(tracker.Decide("notepad", "gaming", presets, DwellMs * 2 + 2));
        Assert.Equal("desk", tracker.Decide("notepad", "gaming", presets, DwellMs * 3 + 2));
    }

    [Fact]
    public void Reset_drops_a_pending_restore()
    {
        var tracker = new AppPresetFocusTracker();
        var presets = new List<LayoutPreset> { Preset("gaming", Bind("chrome")), Preset("desk") };

        Assert.Null(tracker.Decide("chrome", "desk", presets, 0));
        Assert.Equal("gaming", tracker.Decide("chrome", "desk", presets, DwellMs));

        // Every binding was removed while the app-applied preset was live; the
        // restore target no longer refers to anything.
        tracker.Reset();

        Assert.Null(tracker.Decide("notepad", "gaming", presets, DwellMs * 2));
        Assert.Null(tracker.Decide("notepad", "gaming", presets, DwellMs * 3));
    }
}
