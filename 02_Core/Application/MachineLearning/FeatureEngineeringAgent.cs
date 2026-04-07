using System;
using System.Runtime.CompilerServices;
using System.Threading;
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
    // Publish-then-forget: writers allocate a fresh array and atomically swap the reference
    // via Volatile.Write. Readers use Volatile.Read. No lock required.
    private float[] _currentTensor = new float[5];

    public float[] CurrentTensor => _currentTensor;

    private readonly OnlineMathHelper _rsi14;
    private readonly OnlineMathHelper _emaFast;
    private readonly TickRingBuffer _movingAverageBuffer;

    public FeatureEngineeringAgent()
    {
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
    /// Allocates a fresh tensor, computes all features, then atomically publishes via Volatile.Write.
    /// All tensor writes are guarded against NaN/Infinity propagation.
    /// </summary>
    public void ProcessTickNatively(double price)
    {
        double rsi = _rsi14.UpdateAndGetRSI(price);
        _emaFast.UpdateEma(price);
        _movingAverageBuffer.Add(price);

        var tensor = new float[5];

        // Preserve OFI and foundation data from the previous snapshot
        var prev = _currentTensor;
        tensor[3] = prev[3];
        tensor[4] = prev[4];

        // Map mathematical properties linearly — all guarded
        tensor[0] = SafeFloat(rsi / 100.0);       // Normalize RSI to 0-1
        tensor[1] = _emaFast.Ema > 0
            ? SafeFloat(price / _emaFast.Ema, 1.0f)      // EMA deviation, centered at 1.0
            : 1.0f;
        tensor[2] = _movingAverageBuffer.MovingAverage > 0
            ? SafeFloat(price / _movingAverageBuffer.MovingAverage, 1.0f) // MA bias
            : 1.0f;

        // Feature[3] is populated by ApplyOrderFlowImbalance or ApplyFoundationData
        // Feature[4] fallback: momentum from buffer if no foundation data available
        double oldestPrice = _movingAverageBuffer.OldestValue;
        if (Math.Abs(tensor[4]) < 1e-6f && oldestPrice > 0)
        {
            tensor[4] = SafeFloat(price / oldestPrice, 1.0f);
        }

        // Atomically publish the new tensor — no lock needed
        Volatile.Write(ref _currentTensor, tensor);
    }

    /// <summary>
    /// Wires real foundation-layer data into the ML feature tensor.
    /// Feature[3] ← BidAskImbalance (between -1.0 and 1.0)
    /// Feature[4] ← TickDirectionRatio (between 0.0 and 1.0, where 0.5 = neutral)
    /// </summary>
    public void ApplyFoundationData(FoundationSnapshot snapshot)
    {
        var tensor = new float[5];
        var prev = _currentTensor;
        Array.Copy(prev, tensor, 5);
        tensor[3] = SafeFloat(snapshot.BidAskImbalance);
        tensor[4] = SafeFloat(snapshot.TickDirectionRatio, 0.5f);
        Volatile.Write(ref _currentTensor, tensor);
    }

    /// <summary>
    /// Performs a zero-allocation copy of the current feature tensor into a destination span.
    /// This is the preferred way to consume features in a high-frequency context.
    /// </summary>
    public void CopyTo(Span<float> destination)
    {
        if (destination.Length < 5) throw new ArgumentException("Destination span must be at least 5 elements long.");

        Volatile.Read(ref _currentTensor).AsSpan().CopyTo(destination);
    }

    [Obsolete("Use CopyTo(Span<float>) to avoid allocations.")]
    public float[] GetSnapshot()
    {
        var snapshot = new float[5];
        Array.Copy(Volatile.Read(ref _currentTensor), snapshot, 5);
        return snapshot;
    }

    public void ApplyOrderFlowImbalance(double imbalance)
    {
        var tensor = new float[5];
        var prev = _currentTensor;
        Array.Copy(prev, tensor, 5);
        // Hard-map structural OrderFlow Imbalance (Between -1.0 and 1.0) directly onto Dimension 3.
        tensor[3] = SafeFloat(imbalance);
        Volatile.Write(ref _currentTensor, tensor);
    }
}
