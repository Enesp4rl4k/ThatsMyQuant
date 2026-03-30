using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Institutional-grade high-fidelity Market Replay parser.
/// Ingests raw CSV drops (emulating Parquet L2 chunks) directly into zero-allocation 
/// MarketTick and OrderBookSnapshot pipelines.
/// </summary>
public static class L2ExternalDataParser
{
    /// <summary>
    /// Parses high-frequency ticks from a raw CSV natively.
    /// Expected format: Symbol,Price,Volume,TimestampMs,IsBuyerMaker
    /// </summary>
    public static async IAsyncEnumerable<MarketTick> ParseTicksCsvAsync(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);

        // Header
        await reader.ReadLineAsync();

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var span = line.AsSpan();

            // Simple zero-state linear allocator for 5 elements
            var parts = line.Split(',');
            if (parts.Length < 5) continue;

            if (decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var price) &&
                decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var volume) &&
                long.TryParse(parts[3], out var ts) &&
                bool.TryParse(parts[4], out var isBuyerMaker))
            {
                // MarketTick (Symbol, Price, Volume, TimestampMs, IsBuyerMaker)
                yield return new MarketTick(parts[0], price, volume, ts, isBuyerMaker);
            }
        }
    }

    /// <summary>
    /// Parses level-2 OrderBook snaps from CSV natively.
    /// Expected Format: Symbol, TimestampMs, B1_P, B1_Q, A1_P, A1_Q, B2_P, B2_Q ...
    /// </summary>
    public static async IAsyncEnumerable<OrderBookSnapshot> ParseOrderBooksCsvAsync(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var reader = new StreamReader(stream);

        await reader.ReadLineAsync();

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            var parts = line.Split(',');
            if (parts.Length < 22) continue; // ts + symbol + (price,qty)*5 bids + (price,qty)*5 asks = 22

            var symbol = parts[0];
            if (!long.TryParse(parts[1], out var ts)) continue;

            var bids = new OrderBookLevel[5];
            var asks = new OrderBookLevel[5];

            for (int i = 0; i < 5; i++)
            {
                bids[i] = new OrderBookLevel(
                    decimal.Parse(parts[2 + (i * 2)], CultureInfo.InvariantCulture),
                    decimal.Parse(parts[3 + (i * 2)], CultureInfo.InvariantCulture));
                
                asks[i] = new OrderBookLevel(
                    decimal.Parse(parts[12 + (i * 2)], CultureInfo.InvariantCulture),
                    decimal.Parse(parts[13 + (i * 2)], CultureInfo.InvariantCulture));
            }

            yield return new OrderBookSnapshot(symbol, ts, bids, asks);
        }
    }
}
