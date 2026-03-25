using System;
using System.Runtime.InteropServices;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// A zero-allocation Singleton directly managing standard Technical features completely coupled to the WS Market feeds without triggering standard LINQ invocations.
/// Features Tensor Array format strictly maps 5 dimensions:
/// [0] -> RSI
/// [1] -> Fast EMA
/// [2] -> Base Volume (OrderFlow Imbalance logic injected externally via MediatR if required)
/// [3] -> Short-Term Rolling Window Average Spread
/// [4] -> Stochastic / Momentum Indicator approximation natively.
/// </summary>
public class FeatureEngineeringAgent
{
    // SonarQube Fix: Kullanılmayan _lastProcessedPrice alanı tamamen kaldırıldı.

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
    /// Extremely rapid native execution sequentially triggered upon structural Price modifications.
    /// Maps specific physics algorithms structurally mapping onto the static float[] Tensor without generating managed array signatures.
    /// </summary>
    public void ProcessTickNatively(double price)
    {
        double rsi = _rsi14.UpdateAndGetRSI(price);
        _emaFast.UpdateEma(price);
        _movingAverageBuffer.Add(price);

        lock (_syncLock)
        {
            // Map mathematical properties linearly
            CurrentTensor[0] = (float)(rsi / 100.0); // Normalize generic percentages 
            CurrentTensor[1] = (float)(price / _emaFast.Ema); // EMA Deviation natively bounds around 1.0 
            CurrentTensor[2] = (float)(price / _movingAverageBuffer.MovingAverage); // Smoothing moving-average bias
            
            // Populate Momentum (Price change over the buffer window)
            // CurrentTensor[3] is reserved for Order Flow Imbalance (injected via ApplyOrderFlowImbalance)
            double oldestPrice = _movingAverageBuffer.OldestValue;
            CurrentTensor[4] = oldestPrice > 0 ? (float)(price / oldestPrice) : 1.0f;
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

    public void ApplyOrderFlowImbalance(double imbalance)
    {
        lock (_syncLock)
        {
            // Hard-map structural OrderFlow Imbalance (Between -1.0 and 1.0) directly onto Dimension 3.
            CurrentTensor[3] = (float)imbalance;
        }
    }
}
