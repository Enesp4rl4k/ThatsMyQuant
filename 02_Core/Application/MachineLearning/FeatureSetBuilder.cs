using Microsoft.ML.Data;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// ML.NET training data model. Features must exactly match FeatureEngineeringAgent's
/// real-time tensor layout so the trained model works at inference time.
///
/// Label is the forward return (not raw price), because:
///  - Predicting price levels = model just learns "price is ~$60K" → useless
///  - Predicting returns = model learns directional alpha → actionable
/// </summary>
public class CandleMlData
{
    /// <summary>
    /// Forward 1-bar return as a percentage: (next_close - current_close) / current_close * 100.
    /// Positive = bullish, negative = bearish.
    /// </summary>
    [ColumnName("Label")]
    public float ForwardReturn { get; set; }

    /// <summary>
    /// 5-dimensional feature vector matching FeatureEngineeringAgent.CurrentTensor layout:
    ///   [0] RSI(14) / 100              — normalized 0-1
    ///   [1] Price / EMA(9)             — centered around 1.0
    ///   [2] Realized volatility        — stddev of returns, clamped 0-1
    ///   [3] Order Flow Imbalance proxy — volume-weighted buy/sell pressure, -1 to +1
    ///   [4] Price momentum             — price / oldest_price in window
    /// </summary>
    [VectorType(5)]
    [ColumnName("Features")]
    public float[] Features { get; set; } = new float[5];
}

public class CandlePrediction
{
    /// <summary>Predicted forward return (percentage).</summary>
    [ColumnName("Score")]
    public float PredictedReturn { get; set; }
}

/// <summary>
/// Builds training feature sets from historical candle data using the EXACT same feature
/// computations as FeatureEngineeringAgent, ensuring training-inference feature parity.
///
/// This eliminates the critical feature mismatch where the model was trained on one
/// set of features but received a different set at inference time.
/// </summary>
public static class FeatureSetBuilder
{
    private const int RsiPeriod = 14;
    private const int EmaPeriod = 9;
    private const int MomentumWindow = 100;
    private const int VolatilityWarmup = 20;
    private const double MaxVolatility = 0.10;

    // Minimum candles: RSI needs 15, EMA needs 10, momentum needs MomentumWindow
    private const int MinCandles = 101; // MomentumWindow + 1

    /// <summary>
    /// Builds training data with features matching FeatureEngineeringAgent's tensor layout.
    /// Each sample produces the same 5D vector that the real-time agent produces per tick.
    /// </summary>
    public static List<CandleMlData> BuildTrainingSet(List<Candle> historicalCandles)
    {
        if (historicalCandles.Count < MinCandles)
            return new List<CandleMlData>();

        var dataset = new List<CandleMlData>();

        // Pre-compute RSI state using Wilder's smoothing
        var rsiState = new RsiState(RsiPeriod);
        // Pre-compute EMA state
        var emaState = new EmaState(EmaPeriod);
        // Volatility state (Welford's online algorithm)
        var volState = new VolatilityState();
        // Momentum window (ring buffer for oldest price)
        var priceWindow = new double[MomentumWindow];
        int windowCount = 0;
        int windowHead = 0;

        for (int i = 0; i < historicalCandles.Count - 1; i++)
        {
            var candle = historicalCandles[i];
            double price = (double)candle.Close;

            // Update RSI
            rsiState.Update(price);
            // Update EMA
            emaState.Update(price);
            // Update volatility
            volState.Update(price);
            // Update momentum window
            double oldestPrice = windowCount < MomentumWindow
                ? (windowCount > 0 ? priceWindow[0] : price)
                : priceWindow[windowHead];
            priceWindow[windowHead] = price;
            windowHead = (windowHead + 1) % MomentumWindow;
            if (windowCount < MomentumWindow) windowCount++;

            // Skip warmup period — need enough data for all indicators
            if (i < MinCandles - 1) continue;

            // Forward return label
            double nextClose = (double)historicalCandles[i + 1].Close;
            float forwardReturn = price > 0 ? (float)((nextClose - price) / price * 100.0) : 0f;

            // Build feature vector matching FeatureEngineeringAgent exactly
            float[] features = new float[5];

            // [0] RSI(14) / 100 — normalized 0-1
            features[0] = SafeFloat(rsiState.Value / 100.0, 0.5f);

            // [1] Price / EMA(9) — centered around 1.0
            features[1] = emaState.Value > 0
                ? SafeFloat(price / emaState.Value, 1.0f)
                : 1.0f;

            // [2] Realized volatility — stddev of returns, clamped 0-1
            if (volState.Count >= VolatilityWarmup && volState.Variance > 0)
            {
                double stddev = Math.Sqrt(volState.Variance);
                features[2] = SafeFloat(Math.Min(stddev / MaxVolatility, 1.0), 0f);
            }
            else
            {
                features[2] = 0f;
            }

            // [3] Order Flow Imbalance proxy — from candle data, use (Close - Open) / (High - Low)
            // This approximates buy/sell pressure: bullish candle = positive, bearish = negative
            // Normalized to -1 to +1 range, matching ApplyOrderFlowImbalance
            double range = (double)(candle.High - candle.Low);
            double ofiProxy = range > 0 ? (double)(candle.Close - candle.Open) / range : 0;
            features[3] = SafeFloat(Math.Clamp(ofiProxy, -1.0, 1.0), 0f);

            // [4] Price momentum — price / oldest_price in window
            features[4] = oldestPrice > 0
                ? SafeFloat(price / oldestPrice, 1.0f)
                : 1.0f;

            dataset.Add(new CandleMlData
            {
                ForwardReturn = forwardReturn,
                Features = features
            });
        }

        return dataset;
    }

    /// <summary>
    /// Builds a single feature vector from a candle window, matching FeatureEngineeringAgent.
    /// Used for single-point inference from candle data (e.g., during backtesting).
    /// </summary>
    public static float[] BuildSingleFeatureVector(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < MinCandles) return new float[5] { 0.5f, 1.0f, 0f, 0f, 1.0f };

        var rsiState = new RsiState(RsiPeriod);
        var emaState = new EmaState(EmaPeriod);
        var volState = new VolatilityState();

        for (int i = 0; i < candles.Count; i++)
        {
            double price = (double)candles[i].Close;
            rsiState.Update(price);
            emaState.Update(price);
            volState.Update(price);
        }

        double lastPrice = (double)candles[^1].Close;
        double oldestPrice = (double)candles[Math.Max(0, candles.Count - MomentumWindow)].Close;

        var features = new float[5];
        features[0] = SafeFloat(rsiState.Value / 100.0, 0.5f);
        features[1] = emaState.Value > 0 ? SafeFloat(lastPrice / emaState.Value, 1.0f) : 1.0f;

        if (volState.Count >= VolatilityWarmup && volState.Variance > 0)
            features[2] = SafeFloat(Math.Min(Math.Sqrt(volState.Variance) / MaxVolatility, 1.0), 0f);

        double range = (double)(candles[^1].High - candles[^1].Low);
        features[3] = range > 0 ? SafeFloat(Math.Clamp((double)(candles[^1].Close - candles[^1].Open) / range, -1, 1), 0f) : 0f;
        features[4] = oldestPrice > 0 ? SafeFloat(lastPrice / oldestPrice, 1.0f) : 1.0f;

        return features;
    }

    private static float SafeFloat(double value, float fallback)
    {
        float f = (float)value;
        return float.IsNaN(f) || float.IsInfinity(f) ? fallback : f;
    }

    /// <summary>Wilder-smoothed RSI computation matching FeatureEngineeringAgent._rsi14.</summary>
    private sealed class RsiState
    {
        private readonly int _period;
        private double _prevPrice;
        private double _avgGain;
        private double _avgLoss;
        private int _count;

        public double Value { get; private set; } = 50.0;

        public RsiState(int period) => _period = period;

        public void Update(double price)
        {
            if (_count > 0)
            {
                double change = price - _prevPrice;
                double gain = change > 0 ? change : 0;
                double loss = change < 0 ? -change : 0;

                if (_count <= _period)
                {
                    _avgGain += gain;
                    _avgLoss += loss;
                    if (_count == _period)
                    {
                        _avgGain /= _period;
                        _avgLoss /= _period;
                    }
                }
                else
                {
                    // Wilder's smoothing
                    _avgGain = (_avgGain * (_period - 1) + gain) / _period;
                    _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
                }

                if (_count >= _period)
                {
                    Value = _avgLoss == 0 ? 100.0 : 100.0 - 100.0 / (1.0 + _avgGain / _avgLoss);
                }
            }
            _prevPrice = price;
            _count++;
        }
    }

    /// <summary>EMA computation matching FeatureEngineeringAgent._emaFast.</summary>
    private sealed class EmaState
    {
        private readonly double _multiplier;
        private int _count;

        public double Value { get; private set; }

        public EmaState(int period) => _multiplier = 2.0 / (period + 1);

        public void Update(double price)
        {
            if (_count == 0)
                Value = price;
            else
                Value = (price - Value) * _multiplier + Value;
            _count++;
        }
    }

    /// <summary>Welford's online variance for realized volatility, matching FeatureEngineeringAgent.</summary>
    private sealed class VolatilityState
    {
        private double _prevPrice;
        private double _mean;
        private double _m2;
        public int Count { get; private set; }

        public double Variance => Count > 0 ? _m2 / Count : 0;

        public void Update(double price)
        {
            if (_prevPrice > 0)
            {
                double ret = (price - _prevPrice) / _prevPrice;
                Count++;
                double delta = ret - _mean;
                _mean += delta / Count;
                double delta2 = ret - _mean;
                _m2 += delta * delta2;
            }
            _prevPrice = price;
        }
    }
}
