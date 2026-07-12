using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Models.Stocks;

namespace Nexus.Service.Platform.Stocks;

/// <summary>
/// Returns stock/index quotes for the given symbols. Never throws - a symbol
/// whose fetch fails and has no usable stale cache entry comes back with only
/// <see cref="StockQuote.Symbol"/> populated.
/// </summary>
public interface IStockQuoteProvider
{
    /// <summary>
    /// Assumes symbols are already validated and capped, and range is already
    /// whitelisted by the caller.
    /// </summary>
    Task<StockQuotesResponse> GetQuotesAsync(IReadOnlyList<string> symbols, string range);
}
