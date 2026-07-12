using System.Collections.Generic;

namespace Nexus.Service.Models.Stocks;

/// <summary>
/// One symbol's quote for the panel stocks widget. Null-safe by field: a
/// symbol whose upstream fetch failed with no usable cache entry carries only
/// <see cref="Symbol"/>, every other field omitted from the wire response.
/// </summary>
public sealed class StockQuote
{
    public string Symbol { get; set; } = "";
    public string? Name { get; set; }
    public double? Price { get; set; }
    public double? Change { get; set; }
    public double? ChangePercent { get; set; }
    /// <summary>Decimal places Yahoo formats this symbol with (2 for equities/indexes, 4 for FX pairs).</summary>
    public int? PriceHint { get; set; }
    /// <summary>Close prices for the requested range, in time order. Never null; empty when unavailable.</summary>
    public List<double> Series { get; set; } = new();
}

public sealed class StockQuotesResponse
{
    /// <summary>Effective range actually used (falls back to "1d" for an unrecognized request value).</summary>
    public string Range { get; set; } = "";
    public List<StockQuote> Quotes { get; set; } = new();
}
