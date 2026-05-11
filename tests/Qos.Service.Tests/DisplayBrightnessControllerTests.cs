using Qos.Service.Models.Displays;
using Qos.Service.Platform.Displays;

namespace Qos.Service.Tests;

public class DisplayBrightnessControllerTests
{
    [Fact]
    public async Task SetBrightnessAsync_CoalescesQueuedTargetsPerDisplay()
    {
        var provider = new FakeDisplayBrightnessProvider();
        var controller = new DisplayBrightnessController(provider);

        var first = controller.SetBrightnessAsync("display1", 10);
        await Task.Delay(5);
        var second = controller.SetBrightnessAsync("display1", 20);
        var third = controller.SetBrightnessAsync("display1", 30);

        var results = await Task.WhenAll(first, second, third);

        Assert.Equal(new[] { 10, 30 }, provider.Writes);
        Assert.Equal(10, results[0].AppliedBrightness);
        Assert.Equal(30, results[1].AppliedBrightness);
        Assert.Equal(30, results[2].AppliedBrightness);
    }

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
            Thread.Sleep(20);
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
