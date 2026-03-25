using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// A high-fidelity Level 2 Replay Architecture streaming flat CSV files into the identical pipeline buffers utilized by Binance WebSockets.
/// Simulates Tick execution iteratively mapping Orderflow natively across the mutable offline time dimension.
/// </summary>
public class MarketReplayService : IStreamingMarketDataService
{
    private readonly ILogger<MarketReplayService> _logger;
    private readonly MarketReplayClock _replayClock;
    private readonly string _replayFilePath;

    public event Action<MarketTick>? OnTickReceived;
    public event Action<OrderBookSnapshot>? OnOrderBookUpdated;

    public MarketReplayService(ILogger<MarketReplayService> logger, ISystemClock replayClock, IConfiguration config)
    {
        _logger = logger;
        // The DI injection must supply the mutable replay clock instance into ISystemClock in Program.cs.
        _replayClock = replayClock as MarketReplayClock 
            ?? throw new InvalidOperationException("MarketReplayService requires the ISystemClock to inherently be a MarketReplayClock wrapper instance.");
        
        _replayFilePath = config["Backtesting:Level2CsvPath"] ?? "replay_data.csv";
    }

    public async Task ConnectAsync(string[] symbols, CancellationToken cancellationToken)
    {
        await Task.Yield();
        
        if (!File.Exists(_replayFilePath))
        {
            _logger.LogError("File not found to mount L2 execution replay block: {ReplayFilePath}", _replayFilePath);
            return;
        }

        _logger.LogInformation("🚀 Level-2 Market Replay Engaged on {ReplayFilePath}. Simulating WebSocket traffic offline.", _replayFilePath);
        
        // Background loop executing identically to WebSocket Receiver but advancing time synchronously
        _ = Task.Run(() => ExecuteReplayLoopAsync(cancellationToken), cancellationToken);
    }

    private async Task ExecuteReplayLoopAsync(CancellationToken token)
    {
        try
        {
            using var reader = new StreamReader(_replayFilePath);
            string? header = await reader.ReadLineAsync(token); // Skip header

            while (!reader.EndOfStream && !token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Native span splitting
                var span = line.AsSpan();
                
                // Format: Timestamp(ms),Symbol,Bid1,Bid1Qty,Bid2,Bid2Qty,Bid3,Bid3Qty,Bid4,Bid4Qty,Bid5,Bid5Qty,Ask1,Ask1Qty,... LastPrice
                // For simplicity, let's map a strict CSV block specifically.
                // If it's pure Top-Of-Book (Bid1, Ask1): Timestamp,Symbol,Bid1,BidQty,Ask1,AskQty
                
                // We'll process an offline struct native parser here 
                ProcessReplayLine(span);
            }
            _logger.LogInformation("✅ Market Replay execution sequence completed sequentially.");
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Catastrophic file execution error simulating Replay offline.");
        }
    }

    private void ProcessReplayLine(ReadOnlySpan<char> line)
    {
        // VERY BASIC SIMPLIFIED SPLITTER (Assuming Format: UnixMs,Symbol,LastPx,BidPx1,BidQty1,AskPx1,AskQty1)
        int sliceStart = 0;
        int sliceEnd = levelSliceLength(line, ref sliceStart);
        var tsSpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        if (!long.TryParse(tsSpan, out long unixMs)) return;
        
        sliceEnd = levelSliceLength(line, ref sliceStart);
        var symbolSpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        string symbol = new string(symbolSpan);

        sliceEnd = levelSliceLength(line, ref sliceStart);
        var lastPxSpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        double.TryParse(lastPxSpan, out double lastPx);

        sliceEnd = levelSliceLength(line, ref sliceStart);
        var bidPxSpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        double.TryParse(bidPxSpan, out double bidPx);

        sliceEnd = levelSliceLength(line, ref sliceStart);
        var bidQtySpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        double.TryParse(bidQtySpan, out double bidQty);

        sliceEnd = levelSliceLength(line, ref sliceStart);
        var askPxSpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        double.TryParse(askPxSpan, out double askPx);

        sliceEnd = levelSliceLength(line, ref sliceStart);
        var askQtySpan = line.Slice(sliceStart - sliceEnd, sliceEnd);
        double.TryParse(askQtySpan, out double askQty);

        // Advance global simulator execution time natively
        _replayClock.AdvanceTimeUnixMs(unixMs);

        // Native Dispatch -> Route Last Trade Tick
        if (lastPx > 0)
        {
            OnTickReceived?.Invoke(new MarketTick(symbol, (decimal)lastPx, 0.001m, unixMs, false));
        }

        // Native Dispatch -> Route Top-Of-Book Limits
        if (bidPx > 0 && askPx > 0)
        {
            var snapshot = new OrderBookSnapshot(
                symbol, 
                unixMs, 
                new[] { new OrderBookLevel((decimal)bidPx, (decimal)bidQty) }, 
                new[] { new OrderBookLevel((decimal)askPx, (decimal)askQty) }
            );
            OnOrderBookUpdated?.Invoke(snapshot);
        }
    }

    private static int levelSliceLength(ReadOnlySpan<char> span, ref int currentIndex)
    {
        int length = 0;
        while (currentIndex < span.Length && span[currentIndex] != ',')
        {
            length++;
            currentIndex++;
        }
        currentIndex++; // Skip comma
        return length;
    }

    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Replay shutdown sequence requested.");
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
