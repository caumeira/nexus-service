using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Nexus.Service.Auth;
using Nexus.Service.Platform.Stocks;

namespace Nexus.Service.Routes;

public static partial class StockRoutes
{
    private const string DefaultSymbols = "^DJI,^IXIC,^GSPC,EURUSD=X,AAPL,GOOG";
    private const string SymbolPattern = @"^[A-Za-z0-9^.=\-]{1,12}$";
    private const int MaxSymbols = 12;

    private static readonly HashSet<string> AllowedRanges = new(StringComparer.Ordinal)
    {
        "1d", "5d", "1mo", "3mo", "6mo", "1y",
    };

    [GeneratedRegex(SymbolPattern)]
    private static partial Regex SymbolRegex();

    public static void MapStockEndpoints(this WebApplication app)
    {
        app.MapGet("/api/stocks", async (string? symbols, string? range, IStockQuoteProvider provider) =>
        {
            var effectiveRange = ResolveRange(range);
            var effectiveSymbols = ResolveSymbols(symbols);
            return await provider.GetQuotesAsync(effectiveSymbols, effectiveRange);
        }).AllowPanel();
    }

    internal static string ResolveRange(string? range) =>
        range is not null && AllowedRanges.Contains(range) ? range : "1d";

    internal static List<string> ResolveSymbols(string? symbols)
    {
        var raw = string.IsNullOrWhiteSpace(symbols) ? DefaultSymbols : symbols;
        var result = new List<string>(MaxSymbols);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in raw.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0 || !SymbolRegex().IsMatch(trimmed))
            {
                continue;
            }

            var normalized = trimmed.ToUpperInvariant();
            if (!seen.Add(normalized))
            {
                continue;
            }

            result.Add(normalized);
            if (result.Count == MaxSymbols)
            {
                break;
            }
        }
        return result;
    }
}
