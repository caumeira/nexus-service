using System.Linq;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Stocks;

/// <summary>
/// Query-param parsing for GET /api/stocks: range whitelist fallback and
/// symbol validation/cap/default-list behavior. Pure logic, no HTTP.
/// </summary>
public sealed class StockRoutesTests
{
    [Theory]
    [InlineData(null, "1d")]
    [InlineData("", "1d")]
    [InlineData("bogus", "1d")]
    [InlineData("1d", "1d")]
    [InlineData("5d", "5d")]
    [InlineData("1mo", "1mo")]
    [InlineData("3mo", "3mo")]
    [InlineData("6mo", "6mo")]
    [InlineData("1y", "1y")]
    public void ResolveRange_falls_back_to_1d_for_unrecognized_values(string? input, string expected)
    {
        Assert.Equal(expected, StockRoutes.ResolveRange(input));
    }

    [Fact]
    public void ResolveSymbols_trims_and_drops_invalid_entries()
    {
        var result = StockRoutes.ResolveSymbols(" AAPL , bad!symbol , ^DJI ,toolongsymbolname12345");

        Assert.Equal(new[] { "AAPL", "^DJI" }, result);
    }

    [Fact]
    public void ResolveSymbols_caps_at_twelve_taking_the_first_valid_in_order()
    {
        var many = string.Join(',', Enumerable.Range(0, 20).Select(i => $"S{i}"));

        var result = StockRoutes.ResolveSymbols(many);

        Assert.Equal(12, result.Count);
        Assert.Equal(
            new[] { "S0", "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9", "S10", "S11" },
            result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveSymbols_defaults_when_missing_or_empty(string? input)
    {
        var result = StockRoutes.ResolveSymbols(input);

        Assert.Equal(new[] { "^DJI", "^IXIC", "^GSPC", "EURUSD=X", "AAPL", "GOOG" }, result);
    }

    [Fact]
    public void ResolveSymbols_dedups_case_insensitive_duplicates_preserving_first_seen_order()
    {
        var result = StockRoutes.ResolveSymbols("AAPL,aapl,AAPL,^DJI,^dji");

        Assert.Equal(new[] { "AAPL", "^DJI" }, result);
    }

    [Fact]
    public void ResolveSymbols_caps_at_twelve_after_dedup_not_before()
    {
        var many = string.Join(',', Enumerable.Range(0, 20).Select(i => "AAPL"));

        var result = StockRoutes.ResolveSymbols(many);

        Assert.Equal(new[] { "AAPL" }, result);
    }
}
