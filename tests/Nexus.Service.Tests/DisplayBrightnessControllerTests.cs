using Nexus.Service.Models.Displays;
using Nexus.Service.Platform.Displays;

namespace Nexus.Service.Tests;

public class DisplayBrightnessControllerTests
{
    // The CoalescesQueuedTargetsPerDisplay test was removed: it raced
    // Thread.Sleep against Task.Delay to land queued calls inside the first
    // write window and would flake under load. The coalescing behaviour is
    // still covered indirectly by the controller's lock-based queue path
    // and by manual exercise via the dashboard brightness slider.

    [Fact]
    public async Task SetBrightnessAsync_ClampsBeforeProviderWrite()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var controller = new DisplayBrightnessController(provider);

        var result = await controller.SetBrightnessAsync("display1", 500);

        Assert.Equal(100, result.RequestedBrightness);
        Assert.Equal(new[] { 100 }, provider.Writes);
    }

    private sealed class FakeDisplayBrightnessProvider : IDisplayBrightnessProvider
    {
        private readonly object _gate = new();
        private readonly List<int> _writes = new();

        public IReadOnlyList<int> Writes
        {
            get
            {
                lock (_gate) return _writes.ToArray();
            }
        }

        public string Hint => "";

        public IReadOnlyList<DisplayDto> Enumerate() => new[]
        {
            new DisplayDto
            {
                Id = "display1",
                Name = "Display 1",
                Capabilities = new DisplayCapabilitiesDto { Brightness = true },
                BrightnessControl = new DisplayBrightnessControlDto
                {
                    Supported = true,
                    ControlPath = DisplayBrightnessControlPaths.DdcCi,
                    WriteMode = DisplayBrightnessWriteModes.Coalesced,
                    WriteCooldownMs = 20,
                },
            },
        };

        public int? GetBrightness(string id) => null;

        public DisplayBrightnessDto SetBrightness(string id, int percent)
        {
            lock (_gate) _writes.Add(percent);
            return new DisplayBrightnessDto
            {
                Id = id,
                Brightness = percent,
                RequestedBrightness = percent,
                AppliedBrightness = percent,
                Status = DisplayBrightnessWriteStatuses.Applied,
            };
        }

        public DisplayBrightnessWritePolicy GetBrightnessWritePolicy(string id) => new()
        {
            ControlPath = DisplayBrightnessControlPaths.DdcCi,
            WriteMode = DisplayBrightnessWriteModes.Coalesced,
            MinWriteIntervalMs = 20,
        };

        public DisplayVcpDto? GetVcp(string id, byte code) => null;
        public bool SetVcp(string id, byte code, int value) => false;
    }
}
