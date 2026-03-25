using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using System.Collections.Concurrent;
using System.Threading;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

public partial class HftPriceActionAgent : INotificationHandler<Signal>, IFocusedAgent
{
    private readonly ILogger<HftPriceActionAgent> _logger;
    private readonly int _windowSize;
    private readonly ConcurrentDictionary<string, SymbolState> _states = new();
    private string _focusedSymbol = string.Empty;

    public Task ResetAsync(string symbol, CancellationToken ct = default)
    {
        _focusedSymbol = symbol;
        _states.TryRemove(symbol, out _);
        _logger.LogInformation("Agent {AgentId} reset for symbol {Symbol}", nameof(HftPriceActionAgent), symbol);
        return Task.CompletedTask;
    }

    public Task ProcessStepAsync(Candle candle, CancellationToken ct = default)
    {
        var signal = new Signal 
        { 
            Symbol = _focusedSymbol, 
            BestBid = (double)candle.Low, 
            BestAsk = (double)candle.High, 
            Price = candle.Close,
            Timestamp = candle.Timestamp
        };
        return Handle(signal, ct);
    }

    private sealed class SymbolState
    {
        public readonly double[] Buffer;
        public int Head = 0;
        public int Count = 0;
        public double Mean = 0;
        public double M2 = 0; 
        public readonly object SyncLock = new();

        public SymbolState(int windowSize) => Buffer = new double[windowSize];
    }

    public HftPriceActionAgent(ILogger<HftPriceActionAgent> logger, int windowSize = 50)
    {
        _logger = logger;
        _windowSize = windowSize;
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // 1. Data Anchoring (Anchoring to Mid-Price)
        if (!TryCalculateMidPrice(notification, out double midPrice))
            return Task.CompletedTask;

        var state = _states.GetOrAdd(notification.Symbol, static (_, size) => new SymbolState(size), _windowSize);
        
        double zScore = 0;
        bool isExhausted = false;

        // 2. Atomic Statistical Update
        lock (state.SyncLock)
        {
            zScore = UpdateStatisticsAndGetZScore(state, midPrice);
            isExhausted = Math.Abs(zScore) > 2.5d && state.Count >= _windowSize;
        }

        // 3. Execution Logic (Outside the lock to keep it fast)
        if (isExhausted)
        {
            ApplyExhaustionSignal(notification, zScore);
        }

        return Task.CompletedTask;
    }

    private bool TryCalculateMidPrice(Signal signal, out double midPrice)
    {
        midPrice = 0;
        if (!signal.BestBid.HasValue || !signal.BestAsk.HasValue) return false;
        
        midPrice = (signal.BestBid.Value + signal.BestAsk.Value) * 0.5d;
        return true;
    }

    private double UpdateStatisticsAndGetZScore(SymbolState state, double price)
    {
        // Handle Rolling Window logic
        if (state.Count == _windowSize)
        {
            double oldValue = state.Buffer[state.Head];
            double deltaOld = oldValue - state.Mean;
            state.Mean -= deltaOld / _windowSize;
            state.M2 -= deltaOld * (oldValue - state.Mean);
            
            if (state.M2 < 0) state.M2 = 0;
        }
        else
        {
            state.Count++;
        }

        // Update Mean and M2 (Welford's Method)
        state.Buffer[state.Head] = price;
        if (++state.Head == _windowSize) state.Head = 0;

        double delta = price - state.Mean;
        state.Mean += delta / state.Count;
        state.M2 += delta * (price - state.Mean);

        if (state.Count < 2) return 0; // Prevent Division by Zero on (Count - 1)

        double variance = state.M2 / (state.Count - 1);
        double stdDev = Math.Sqrt(variance);

        return stdDev < 1e-9 ? 0 : (price - state.Mean) / stdDev;
    }

    private void ApplyExhaustionSignal(Signal notification, double zScore)
    {
        notification.PriceActionSignal = zScore > 0 ? PriceActionType.ExhaustionHigh : PriceActionType.ExhaustionLow;
        notification.AddReason(SignalReasonFlags.PriceActionExhaustion);

        LogPriceActionSweep(_logger, notification.Symbol, zScore, notification.PriceActionSignal.ToString());
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "[PA DETECTED] Liquidity Sweep on {Symbol}. Z-Score: {ZScore:F2}. State: {State}")]
    private static partial void LogPriceActionSweep(ILogger logger, string symbol, double zScore, string state);
}
