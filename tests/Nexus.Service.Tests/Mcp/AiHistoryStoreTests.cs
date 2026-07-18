using Nexus.Service.Mcp.History;
using Xunit;

namespace Nexus.Service.Tests.Mcp;

/// <summary>
/// AiHistoryRetention.PickTier is a pure function shared by every store
/// implementation's tier-selection logic, so it is tested once here rather
/// than duplicated per store - the store-behavior tests (raw round trip,
/// rollup, pruning, summary, thinning, events, reopen) live in
/// AiHistoryStoreSpec, parameterized over SqliteAiHistoryStore and
/// BinaryAiHistoryStore.
/// </summary>
public sealed class AiHistoryStoreTests
{
    [Theory]
    [InlineData(1, AiHistoryTier.Raw)]
    [InlineData(30, AiHistoryTier.Raw)]
    [InlineData(31, AiHistoryTier.OneMinute)]
    [InlineData(1440, AiHistoryTier.OneMinute)]
    [InlineData(1441, AiHistoryTier.FiveMinute)]
    [InlineData(10080, AiHistoryTier.FiveMinute)]
    public void PickTier_selects_by_window_length(int minutes, AiHistoryTier expected)
    {
        Assert.Equal(expected, AiHistoryRetention.PickTier(minutes));
    }
}
