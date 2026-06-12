using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Lighting.Mappings;

/// <summary>
/// The tracker must treat provider-authored UVs as the pristine baseline,
/// recognize its own applied arrays by reference (never re-snapshotting
/// them), and survive frame rebuilds without losing user layouts.
/// </summary>
public class ContributorFrameLayoutsTests
{
    private const string Id = "np50:port1:dev1";

    [Fact]
    public void Provider_uvs_become_default_and_survive_user_overrides()
    {
        var tracker = new ContributorFrameLayouts();
        var settings = new NexusSettings();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };

        tracker.Refresh(frame, settings);
        Assert.Equal(0.2f, frame.LedU![0]);

        // User edits one LED; the other keeps the provider default.
        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.99f, V = 0.99f },
        };
        tracker.Refresh(frame, settings);
        Assert.Equal(0.99f, frame.LedU![0]);
        Assert.Equal(0.8f, frame.LedU[1]);
        Assert.Equal(0.7f, frame.LedV![1]);
    }

    [Fact]
    public void Own_applied_arrays_are_not_resnapshotted_as_defaults()
    {
        var tracker = new ContributorFrameLayouts();
        var settings = new NexusSettings();
        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.99f, V = 0.99f },
        };
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };

        tracker.Refresh(frame, settings);
        // Same frame instance comes back on the next topology refresh, now
        // carrying OUR arrays; removing the override must restore the
        // provider default, not the override-tainted values.
        tracker.Refresh(frame, settings);
        settings.Devices.DeviceLedOverrides.Remove(Id);
        tracker.Refresh(frame, settings);
        Assert.Equal(0.2f, frame.LedU![0]);

        var (defU, _) = tracker.GetDefaults(Id);
        Assert.NotNull(defU);
        Assert.Equal(0.2f, defU![0]);
    }

    [Fact]
    public void Uvless_frames_get_linear_layout()
    {
        var tracker = new ContributorFrameLayouts();
        var frame = new DeviceFrame(0, Id, ledCount: 3);
        tracker.Refresh(frame, new NexusSettings());
        Assert.NotNull(frame.LedU);
        Assert.Equal(new[] { 0f, 0.5f, 1f }, frame.LedU);
    }

    [Fact]
    public void Count_change_drops_stale_defaults()
    {
        var tracker = new ContributorFrameLayouts();
        var settings = new NexusSettings();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };
        tracker.Refresh(frame, settings);

        var resized = new DeviceFrame(0, Id, ledCount: 4);
        tracker.Refresh(resized, settings);
        Assert.Equal(4, resized.LedU!.Length);
        Assert.Equal(0f, resized.LedU[0]);
    }

    [Fact]
    public void Route_side_apply_does_not_contaminate_provider_defaults()
    {
        var tracker = new ContributorFrameLayouts();
        var settings = new NexusSettings();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };
        tracker.Refresh(frame, settings);

        // Route-side refresh (editor save) with a user override applied via
        // the tracker-aware Apply, then a bridge rebuild reusing the SAME
        // frame instance: the provider baseline must survive.
        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.99f, V = 0.99f },
        };
        var resolved = LedLayoutResolver.ResolveSeeded(
            Id, frame.LedCount, tracker.GetDefaults(Id).U, tracker.GetDefaults(Id).V, settings);
        tracker.Apply(frame, resolved);
        tracker.Refresh(frame, settings);

        settings.Devices.DeviceLedOverrides.Remove(Id);
        tracker.Refresh(frame, settings);
        Assert.Equal(0.2f, frame.LedU![0]);
        Assert.Equal(0.8f, frame.LedU[1]);
    }

    [Fact]
    public void Structure_seeds_become_pristine_defaults_and_survive_overrides()
    {
        var tracker = new ContributorFrameLayouts();
        var settings = new NexusSettings();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        var seedU = new[] { 0.25f, 0.75f };
        var seedV = new[] { 0.1f, 0.9f };

        tracker.Refresh(frame, settings, overrides: null, seedU, seedV);
        Assert.Equal(seedU, frame.LedU);
        Assert.Equal(seedV, frame.LedV);

        settings.Devices.DeviceLedOverrides[Id] = new()
        {
            new SegmentLedOverride { Segment = 0, LedIndex = 0, U = 0.99f, V = 0.99f },
        };
        tracker.Refresh(frame, settings, overrides: null, seedU, seedV);
        Assert.Equal(0.99f, frame.LedU![0]);
        Assert.Equal(0.75f, frame.LedU[1]);

        var (defU, defV) = tracker.GetDefaults(Id);
        Assert.Equal(seedU, defU);
        Assert.Equal(seedV, defV);
    }

    [Fact]
    public void Structure_seed_wins_over_frame_authored_uvs_and_reseeds_reused_frames()
    {
        var tracker = new ContributorFrameLayouts();
        var settings = new NexusSettings();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };

        tracker.Refresh(frame, settings, overrides: null, new[] { 0.4f, 0.6f }, new[] { 0.5f, 0.5f });
        Assert.Equal(0.4f, frame.LedU![0]);

        // Partition reshape: the same frame instance survives (it carries OUR
        // applied arrays) but its zone's seed changed; the new seed must
        // replace the baseline instead of being skipped by the
        // own-array-by-reference rule.
        tracker.Refresh(frame, settings, overrides: null, new[] { 0.45f, 0.65f }, new[] { 0.55f, 0.35f });
        Assert.Equal(0.45f, frame.LedU![0]);
        var (defU, _) = tracker.GetDefaults(Id);
        Assert.Equal(new[] { 0.45f, 0.65f }, defU);
    }

    [Fact]
    public void Seed_length_mismatch_falls_back_to_frame_snapshot()
    {
        var tracker = new ContributorFrameLayouts();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };

        tracker.Refresh(frame, new NexusSettings(), overrides: null, new[] { 0.4f }, new[] { 0.5f });
        Assert.Equal(0.2f, frame.LedU![0]);
        var (defU, _) = tracker.GetDefaults(Id);
        Assert.Equal(0.2f, defU![0]);
    }

    [Fact]
    public void Prune_drops_state_for_removed_devices()
    {
        var tracker = new ContributorFrameLayouts();
        var frame = new DeviceFrame(0, Id, ledCount: 2);
        frame.LedU = new[] { 0.2f, 0.8f };
        frame.LedV = new[] { 0.3f, 0.7f };
        tracker.Refresh(frame, new NexusSettings());

        tracker.Prune(new[] { "other" });
        var (defU, defV) = tracker.GetDefaults(Id);
        Assert.Null(defU);
        Assert.Null(defV);
    }
}
