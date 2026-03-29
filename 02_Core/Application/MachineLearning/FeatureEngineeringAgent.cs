using System;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Zero-allocation singleton that maintains a 5-dimensional feature tensor from live tick data.
/// Updated O(1) per tick via online algorithms (no LINQ, no allocations in hot path).
///
/// Feature tensor layout:
///   [0] RSI(14) / 100              — momentum oscillator, normalized 0-1
///   [1] Price / EMA(9)             — EMA deviation ratio, centers around 1.0
///   [2] Realized volatility        — stddev of returns over rolling window, clamped 0-1
///   [3] Order Flow Imbalance       — injected from order book via ApplyOrderFlowImbalance, -1 to +1
///   [4] Price momentum             — price / oldest_price in window, centers around 1.0
///
/// All features are guarded against NaN/Infinity — any computation fault falls back to a neutral value.
/// </summary>
public class FeatureEngineeringAgent
{
    public float[] CurrentTensor { get; }

    private readonly OnlineMathHelper _rsi14;
    private readonly OnlineMathHelper _emaFast;
    private readonly TickRingBuffer _priceBuffer;
    private readonly object _syncLock = new();

    // Online variance tracking (Welford's algorithm) for realized volatility
    private double _prevPrice;
    private double _returnMean;
    private double _returnM2;
    private int _returnCount;
    private const int VolatilityWarmup = 20;
    private const double MaxVolatility = 0.10; // 10% return stddev = clamp ceiling

    public FeatureEngineeringAgent()
    {
        CurrentTensor = new float[5];

        _rsi14 = new OnlineMathHelper(14);
        _emaFast = new OnlineMathHelper(9);
        _priceBuffer = new TickRingBuffer(100);
    }

    /// <summary>
    /// Processes a single price tick, updating all 4 price-derived features in O(1).
    /// Feature[3] (OFI) is updated separately via <see cref="ApplyOrderFlowImbalance"/>.
    /// </summary>
    public void ProcessTickNatively(double price, double volume = 0)
    {
        double rsi = _rsi14.UpdateAndGetRSI(price);
        _emaFast.UpdateEma(price);
        _priceBuffer.Add(price);

        lock (_syncLock)
        {
            // Online return variance (Welford's algorithm) for realized volatility
            if (_prevPrice > 0)
            {
                double ret = (price - _prevPrice) / _prevPrice;
                _returnCount++;
                double delta = ret - _returnMean;
                _returnMean += delta / _returnCount;
                double delta2 = ret - _returnMean;
                _returnM2 += delta * delta2;
            }
            _prevPrice = price;

            // [0] RSI normalized to 0-1
            CurrentTensor[0] = SafeFloat(rsi / 100.0, 0.5f);

            // [1] Price / EMA deviation — centers around 1.0
            CurrentTensor[1] = _emaFast.Ema > 0
                ? SafeFloat(price / _emaFast.Ema, 1.0f)
                : 1.0f;

            // [2] Realized volatility — stddev of returns, normalized by MaxVolatility, clamped 0-1
            if (_returnCount >= VolatilityWarmup && _returnM2 > 0)
            {
                double variance = _returnM2 / _returnCount;
                double stddev = Math.Sqrt(variance);
                CurrentTensor[2] = SafeFloat(Math.Min(stddev / MaxVolatility, 1.0), 0f);
            }
            else
            {
                CurrentTensor[2] = 0f; // Not enough data yet
            }

            // [3] OFI — injected externally via ApplyOrderFlowImbalance, not touched here

            // [4] Momentum — price / oldest_price in buffer window
            double oldestPrice = _priceBuffer.OldestValue;
            CurrentTensor[4] = oldestPrice > 0
                ? SafeFloat(price / oldestPrice, 1.0f)
                : 1.0f;
        }
    }

    public float[] GetSnapshot()
    {
        var snapshot = new float[5];
        lock (_syncLock)
        {
            Array.Copy(CurrentTensor, snapshot, 5);
        }
        return snapshot;
    }

    /// <summary>
    /// Injects the order book imbalance (bid-ask pressure) into Feature[3].
    /// Called from TickPipeline when a new order book snapshot arrives.
    /// Value range: -1.0 (asks dominate) to +1.0 (bids dominate).
    /// </summary>
    public void ApplyOrderFlowImbalance(double imbalance)
    {
        lock (_syncLock)
        {
            CurrentTensor[3] = SafeFloat(imbalance, 0f);
        }
    }

    /// <summary>
    /// Guards against NaN/Infinity — returns the fallback value if the computation faulted.
    /// </summary>
    internal static float SafeFloat(double value, float fallback)
    {
        float f = (float)value;
        return float.IsNaN(f) || float.IsInfinity(f) ? fallback : f;
    }
}
