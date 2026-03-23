using Microsoft.Extensions.Logging;
using MediatR;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using System.Collections.Concurrent;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Optimized High-frequency Price Action agent.
/// Built with ultra-HFT methodologies:
/// - O(1) Welford's Sample Variance with Reciprocal Multiplication logic.
/// - Orderbook Mid-Price execution anchor (bypasses lagging trade prints).
/// - GC-free Array indexing (Modulo division destroyed).
/// - Strongly-typed Execution States.
/// </summary>
public partial class HftPriceActionAgent : INotificationHandler<Signal>
{
    private readonly ILogger<HftPriceActionAgent> _logger;
    private readonly int _windowSize;
    private readonly ConcurrentDictionary<string, SymbolState> _states = new();

    private sealed class SymbolState
    {
        public readonly double[] Buffer;
        public int Head = 0;
        public int Count = 0;

        public double Mean = 0;
        public double M2 = 0; // Sum of squared deviations from the mean

        public readonly object SyncLock = new();

        public SymbolState(int windowSize)
        {
            Buffer = new double[windowSize];
        }
    }

    public HftPriceActionAgent(ILogger<HftPriceActionAgent> logger, int windowSize = 50)
    {
        _logger = logger;
        _windowSize = windowSize;
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // 1. True L2 Microstructure anchoring: Ignore lagging ticks and use Orderbook Mid-Price
        if (!notification.BestBid.HasValue || !notification.BestAsk.HasValue) 
            return Task.CompletedTask;

        double midPrice = (notification.BestBid.Value + notification.BestAsk.Value) * 0.5d;

        double zScore = 0;
        bool detected = false;
        
        var state = _states.GetOrAdd(notification.Symbol, static (_, size) => new SymbolState(size), _windowSize);

        lock (state.SyncLock)
        {
            double oldValue = 0;

            if (state.Count == _windowSize)
            {
                oldValue = state.Buffer[state.Head];

                double oldMean = state.Mean;
                state.Mean = (state.Mean * _windowSize - oldValue) / (_windowSize - 1);
                state.M2 -= (oldValue - oldMean) * (oldValue - state.Mean);
                
                if (state.M2 < 0) state.M2 = 0;
            }
            else
            {
                state.Count++;
            }

            state.Buffer[state.Head] = midPrice;

            // 2. Micro-Optimization: Replaced expensive CPU integer modulo (%) with direct bounds check
            if (++state.Head == _windowSize) state.Head = 0;

            // 3. Micro-Optimization: Reciprocal multiplication vs division
            double invCount = 1.0d / state.Count;

            double delta = midPrice - state.Mean;
            state.Mean += delta * invCount;
            double delta2 = midPrice - state.Mean;
            state.M2 += delta * delta2;

            if (state.Count < _windowSize) return Task.CompletedTask;

            // 4. Statistical Rigor: Using Sample Variance (N-1) for real-world subsets
            double sampleVariance = state.M2 / (state.Count - 1); 
            double stdDev = Math.Sqrt(sampleVariance);

            zScore = stdDev == 0 ? 0 : (midPrice - state.Mean) / stdDev;

            if (Math.Abs(zScore) > 2.5d) 
                detected = true;
        }

        if (detected)
        {
            notification.PriceActionSignal = zScore > 0 ? PriceActionType.ExhaustionHigh : PriceActionType.ExhaustionLow;
            notification.AddReason(SignalReasonFlags.PriceActionExhaustion);

            LogPriceActionSweep(_logger, notification.Symbol, zScore, notification.PriceActionSignal.ToString());
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "[PA DETECTED] Liquidity Sweep on {Symbol}. Z-Score: {ZScore:F2}. State: {State}")]
    private static partial void LogPriceActionSweep(ILogger logger, string symbol, double zScore, string state);
}
