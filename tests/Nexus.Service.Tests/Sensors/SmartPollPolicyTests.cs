using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class SmartPollPolicyTests
{
    [Theory]
    [InlineData(0, uint.MaxValue)]
    [InlineData(-5, uint.MaxValue)]
    [InlineData(1, 1u)]
    [InlineData(30, 30u)]
    [InlineData(300, 300u)]
    [InlineData(9000, 300u)]
    public void ToCycleCount_mapsSecondsOntoTheWalkCadence(int seconds, uint expected)
    {
        Assert.Equal(expected, SmartPollPolicy.ToCycleCount(seconds));
    }

    [Fact]
    public void NeverIsUnreachable_notMerelySlow()
    {
        // LHM compares ++cycle >= count, so uint.MaxValue is never met in any
        // realistic uptime - "Never" must not degrade into "eventually".
        Assert.Equal(uint.MaxValue, SmartPollPolicy.ToCycleCount(SmartPollPolicy.NeverSeconds));
    }

    [Fact]
    public void Sanitize_clampsOutOfRangeAndDropsBlankKeys()
    {
        var result = SmartPollPolicy.Sanitize(new Dictionary<string, int>
        {
            ["/hdd/0"] = 5,
            ["/hdd/1"] = 99999,
            ["/hdd/2"] = -1,
            ["   "] = 30,
        });

        Assert.Equal(3, result.Count);
        Assert.Equal(5, result["/hdd/0"]);
        Assert.Equal(SmartPollPolicy.MaxSeconds, result["/hdd/1"]);
        Assert.Equal(SmartPollPolicy.NeverSeconds, result["/hdd/2"]);
    }

    [Fact]
    public void Choices_areWithinTheSupportedRange()
    {
        foreach (var choice in SmartPollPolicy.Choices)
        {
            Assert.Equal(choice, SmartPollPolicy.ClampSeconds(choice));
        }
        Assert.Contains(SmartPollPolicy.NeverSeconds, SmartPollPolicy.Choices);
        Assert.Contains(SmartPollPolicy.MinSeconds, SmartPollPolicy.Choices);
        Assert.Contains(SmartPollPolicy.MaxSeconds, SmartPollPolicy.Choices);
    }
}
