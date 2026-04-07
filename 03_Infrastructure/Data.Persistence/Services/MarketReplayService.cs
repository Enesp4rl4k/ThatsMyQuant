using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// High-fidelity Market Replay Engine for Level-2 backtesting.
/// Streams historical tick and depth data into the Swarm Engine.
/// </summary>
public sealed class MarketReplayService : IStreamingMarketDataService
{
    private readonly string _dataPath;
    private bool _isRunning;
    private Task? _replayTask;

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public MarketReplayService(string dataPath)
    {
        _dataPath = dataPath;
    }

    public Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        _isRunning = true;
        _replayTask = RunReplayLoopAsync(cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RunReplayLoopAsync(CancellationToken ct)
    {
        if (!File.Exists(_dataPath))
        {
            throw new FileNotFoundException($"Replay data file not found: {_dataPath}");
        }

        using var reader = new StreamReader(_dataPath);
        // Header: Type,Symbol,Price,Qty,Timestamp,IsMaker,DepthData
        await reader.ReadLineAsync();

        while (_isRunning && !reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var parts = line.Split(',', 7);
                if (parts.Length < 5)
                {
                    continue;
                }

                var type = parts[0];
                var symbol = parts[1];
                var price = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
                var qty = decimal.Parse(parts[3], CultureInfo.InvariantCulture);
                var ts = long.Parse(parts[4]);

                if (type == "TICK")
                {
                    var isMaker = bool.Parse(parts[5]);
                    OnTickReceived?.Invoke(new MarketTick(symbol, price, qty, ts, isMaker));
                }
                else if (type == "DEPTH")
                {
                    var snapshot = TryParseDepthSnapshot(symbol, ts, parts.Length >= 7 ? parts[6] : null);
                    OnOrderBookUpdated?.Invoke(snapshot ?? CreateFallbackSnapshot(symbol, ts, price, qty));
                }

                // Simulate realistic arrival (optional: sleep based on timestamps)
                // In a true HFT backtest, we might run as fast as possible or match TS delta.
            }
            catch (Exception)
            {
                // Skip malformed lines in replay
            }
        }
        
        _isRunning = false;
    }

    public Task SubscribeAsync(string symbol) => Task.CompletedTask;
    public Task UnsubscribeAsync(string symbol) => Task.CompletedTask;
    public Task DisconnectAsync()
    {
        _isRunning = false;
        return Task.CompletedTask;
    }

    private static OrderBookSnapshot? TryParseDepthSnapshot(string symbol, long timestampMs, string? depthData)
    {
        if (string.IsNullOrWhiteSpace(depthData))
        {
            return null;
        }

        return TryParseJsonDepthSnapshot(symbol, timestampMs, depthData)
            ?? TryParseCompactDepthSnapshot(symbol, timestampMs, depthData);
    }

    private static OrderBookSnapshot? TryParseJsonDepthSnapshot(string symbol, long timestampMs, string depthData)
    {
        try
        {
            using var document = JsonDocument.Parse(depthData);
            var root = document.RootElement;
            if (!root.TryGetProperty("bids", out var bidsElement) || !root.TryGetProperty("asks", out var asksElement))
            {
                return null;
            }

            var bids = ParseJsonLevels(bidsElement);
            var asks = ParseJsonLevels(asksElement);
            if (bids.Length == 0 || asks.Length == 0)
            {
                return null;
            }

            return new OrderBookSnapshot(symbol, timestampMs, bids, asks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OrderBookLevel[] ParseJsonLevels(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return [];

        var levels = new List<OrderBookLevel>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
            {
                var price = ParseDecimal(item[0]);
                var quantity = ParseDecimal(item[1]);
                if (price > 0 && quantity > 0) levels.Add(new OrderBookLevel(price, quantity));
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                decimal price = TryGetJsonProperty(item, "price");
                decimal quantity = TryGetJsonProperty(item, "quantity");
                if (price > 0 && quantity > 0) levels.Add(new OrderBookLevel(price, quantity));
            }
        }
        return levels.ToArray();
    }

    private static decimal TryGetJsonProperty(JsonElement element, string propName)
    {
        return element.TryGetProperty(propName, out var prop) ? ParseDecimal(prop) : 0m;
    }

    private static decimal ParseDecimal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0m
        };
    }

    private static OrderBookSnapshot? TryParseCompactDepthSnapshot(string symbol, long timestampMs, string depthData)
    {
        var segments = depthData.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        OrderBookLevel[] bids = [];
        OrderBookLevel[] asks = [];

        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var side = segment[..separatorIndex].Trim();
            var levelsText = segment[(separatorIndex + 1)..].Trim();
            var levels = ParseCompactLevels(levelsText);

            if (side.Equals("bids", StringComparison.OrdinalIgnoreCase))
            {
                bids = levels;
            }
            else if (side.Equals("asks", StringComparison.OrdinalIgnoreCase))
            {
                asks = levels;
            }
        }

        if (bids.Length == 0 || asks.Length == 0)
        {
            return null;
        }

        return new OrderBookSnapshot(symbol, timestampMs, bids, asks);
    }

    private static OrderBookLevel[] ParseCompactLevels(string text)
    {
        return text
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(level =>
            {
                var parts = level.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                    !decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var quantity))
                {
                    return (OrderBookLevel?)null;
                }

                return new OrderBookLevel(price, quantity);
            })
            .Where(level => level.HasValue)
            .Select(level => level!.Value)
            .ToArray();
    }

    private static OrderBookSnapshot CreateFallbackSnapshot(string symbol, long timestampMs, decimal price, decimal qty)
    {
        var syntheticSpread = Math.Max(0.01m, price * 0.0001m);
        return new OrderBookSnapshot(
            symbol,
            timestampMs,
            [new OrderBookLevel(price - syntheticSpread, qty * 2)],
            [new OrderBookLevel(price + syntheticSpread, qty * 2)]);
    }
}
