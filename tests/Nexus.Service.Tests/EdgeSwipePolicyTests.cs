using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Edge-swipe suppression is presence-gated: any touch-expected display in
/// the snapshot asserts the policy, independent of the mapping outcome - an
/// already-correct generic-tier box (Decide says NoPanel) and a
/// companion-mismatched catalog digitizer (NoDigitizer) still have touch
/// glass attached.
/// </summary>
public sealed class EdgeSwipePolicyTests
{
    private const string Y70MonitorPath =
        @"\\?\DISPLAY#RTK0004#5&1264575b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Y70DigitizerPath =
        @"\\?\HID#VID_27C0&PID_0859&MI_00&Col01#a&2d89150c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string PlainMonitorPath =
        @"\\?\DISPLAY#DEL4099#5&1264575b&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private sealed class FakeSnapshotSource : ITouchMapSnapshotSource
    {
        private readonly TouchMapSnapshot? _snapshot;
        public FakeSnapshotSource(TouchMapSnapshot? snapshot) => _snapshot = snapshot;
        public Task<TouchMapSnapshot?> GetSnapshotAsync(CancellationToken ct = default) => Task.FromResult(_snapshot);
    }

    private sealed class FakeRegistryWriter : IDigimonRegistryWriter
    {
        public void Write(string digitizerInterfacePath, string monitorInterfacePath) { }
    }

    private sealed class FakeDevnodeRestarter : ITouchDigitizerDevnodeRestarter
    {
        public bool Restart(string digitizerInterfacePath) => true;
    }

    private sealed class FakeEdgeSwipePolicy : IEdgeSwipePolicy
    {
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }

        public bool EnsureDisabled()
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return true;
        }
    }

    private static TouchMappingGuard GuardFor(TouchMapSnapshot? snapshot, FakeEdgeSwipePolicy policy) => new(
        new FakeSnapshotSource(snapshot), new FakeRegistryWriter(), new FakeDevnodeRestarter(), policy,
        verifyTimeout: TimeSpan.FromMilliseconds(40), verifyPollInterval: TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Asserts_on_a_correctly_mapped_catalog_panel()
    {
        var policy = new FakeEdgeSwipePolicy();
        var guard = GuardFor(new TouchMapSnapshot
        {
            Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            Digitizers = { new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" } },
        }, policy);

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.AlreadyCorrect, outcome.Result);
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public async Task Asserts_when_the_panel_display_is_present_without_its_digitizer()
    {
        var policy = new FakeEdgeSwipePolicy();
        var guard = GuardFor(new TouchMapSnapshot
        {
            Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
        }, policy);

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.NoDigitizer, outcome.Result);
        Assert.Equal(1, policy.Calls);
    }

    [Fact]
    public async Task Skips_when_no_touch_expected_display_is_attached()
    {
        var policy = new FakeEdgeSwipePolicy();
        var guard = GuardFor(new TouchMapSnapshot
        {
            Displays = { new TouchMapDisplayInfo { Id = "desk-1", MonitorInterfacePath = PlainMonitorPath } },
        }, policy);

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.NoPanel, outcome.Result);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task Skips_when_no_helper_snapshot_is_available()
    {
        var policy = new FakeEdgeSwipePolicy();
        var guard = GuardFor(null, policy);

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.NoHelper, outcome.Result);
        Assert.Equal(0, policy.Calls);
    }

    [Fact]
    public async Task Policy_write_failure_does_not_fail_the_mapping_pass()
    {
        var policy = new FakeEdgeSwipePolicy { Throw = new UnauthorizedAccessException("hklm denied") };
        var guard = GuardFor(new TouchMapSnapshot
        {
            Displays = { new TouchMapDisplayInfo { Id = "y70-1", MonitorInterfacePath = Y70MonitorPath } },
            Digitizers = { new TouchMapDigitizerInfo { InterfacePath = Y70DigitizerPath, AssociatedDisplayId = "y70-1" } },
        }, policy);

        var outcome = await guard.RunPassAsync();

        Assert.Equal(TouchMappingPassResult.AlreadyCorrect, outcome.Result);
        Assert.Equal(1, policy.Calls);
    }
}
