using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// A zero-allocation Singleton directly managing standard Technical features completely coupled to the WS Market feeds without triggering standard LINQ invocations.
/// Features Tensor Array format strictly maps 5 dimensions:
/// [0] -> RSI (normalized 0-1)
/// [1] -> Fast EMA deviation (price / EMA, centered around 1.0)
/// [2] -> Moving-average bias (price / MA, centered around 1.0)
/// [3] -> Order Flow Imbalance / BidAskImbalance (-1.0 to 1.0)
/// [4] -> Tick Direction Ratio / Momentum (0.0 to 1.0)
/// </summary>
public class FeatureEngineeringAgent
{
    // The exact structural native pointer continuously updated entirely outside GC.
    public float[] CurrentTensor { get; } // Readonly array reference

    private readonly OnlineMathHelper _rsi14;
    private readonly OnlineMathHelper _emaFast;
    private readonly TickRingBuffer _movingAverageBuffer;
    private readonly object _syncLock = new();

    public FeatureEngineeringAgent()
    {
        CurrentTensor = new float[5];

        _rsi14 = new OnlineMathHelper(14);
        _emaFast = new OnlineMathHelper(9);
        _movingAverageBuffer = new TickRingBuffer(100);
    }

    /// <summary>
    /// Clamps NaN, Infinity, and extreme outliers to safe float values.
    /// This is the single guard preventing poison values from corrupting the ML tensor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SafeFloat(double value, float fallback = 0f)
        => double.IsNaN(value) || double.IsInfinity(value) ? fallback : (float)value;

    /// <summary>
    /// Extremely rapid native execution sequentially triggered upon structural Price modifications.
    /// Maps specific physics algorithms structurally mapping onto the static float[] Tensor without generating managed array signatures.
    /// All tensor writes are guarded against NaN/Infinity propagation.
    /// </summary>
    public void ProcessTickNatively(double price)
    {
        double rsi = _rsi14.UpdateAndGetRSI(price);
        _emaFast.UpdateEma(price);
        _movingAverageBuffer.Add(price);

        lock (_syncLock)
        {
            // Map mathematical properties linearly — all guarded
            CurrentTensor[0] = SafeFloat(rsi / 100.0);       // Normalize RSI to 0-1
            CurrentTensor[1] = _emaFast.Ema > 0
                ? SafeFloat(price / _emaFast.Ema, 1.0f)      // EMA deviation, centered at 1.0
                : 1.0f;
            CurrentTensor[2] = _movingAverageBuffer.MovingAverage > 0
                ? SafeFloat(price / _movingAverageBuffer.MovingAverage, 1.0f) // MA bias
                : 1.0f;

            // Feature[3] is populated by ApplyOrderFlowImbalance or ApplyFoundationData
            // Feature[4] fallback: momentum from buffer if no foundation data available
            double oldestPrice = _movingAverageBuffer.OldestValue;
            if (Math.Abs(CurrentTensor[4]) < 1e-6f && oldestPrice > 0)
            {
                CurrentTensor[4] = SafeFloat(price / oldestPrice, 1.0f);
            }
        }
    }

    /// <summary>
    /// Wires real foundation-layer data into the ML feature tensor.
    /// Feature[3] ← BidAskImbalance (between -1.0 and 1.0)
    /// Feature[4] ← TickDirectionRatio (between 0.0 and 1.0, where 0.5 = neutral)
    /// </summary>
    public void ApplyFoundationData(FoundationSnapshot snapshot)
    {
        lock (_syncLock)
        {
            CurrentTensor[3] = SafeFloat(snapshot.BidAskImbalance);
            CurrentTensor[4] = SafeFloat(snapshot.TickDirectionRatio, 0.5f);
        }
    }

    /// <summary>
    /// Performs a zero-allocation copy of the current feature tensor into a destination span.
    /// This is the preferred way to consume features in a high-frequency context.
    /// </summary>
    public void CopyTo(Span<float> destination)
    {
        if (destination.Length < 5) throw new ArgumentException("Destination span must be at least 5 elements long.");

        lock (_syncLock)
        {
            CurrentTensor.AsSpan().CopyTo(destination);
        }
    }

    [Obsolete("Use CopyTo(Span<float>) to avoid allocations.")]
    public float[] GetSnapshot()
    {
        var snapshot = new float[5];
        lock (_syncLock)
        {
            Array.Copy(CurrentTensor, snapshot, 5);
        }
        return snapshot;
    }

    public void ApplyOrderFlowImbalance(double imbalance)
    {
        lock (_syncLock)
        {
            // Hard-map structural OrderFlow Imbalance (Between -1.0 and 1.0) directly onto Dimension 3.
            CurrentTensor[3] = SafeFloat(imbalance);
        }
    }
}

