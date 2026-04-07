using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Provides SIMD-accelerated mathematical operations for HFT data processing.
/// Utilizes AVX/AVX2 instructions where available to process multiple ticks in a single CPU cycle.
/// </summary>
public static class VectorizedMathUtils
{
    /// <summary>
    /// Calculates the sum of a price array using SIMD (AVX 256-bit).
    /// Processes 8 floats (or 4 doubles) at a time.
    /// </summary>
    public static float SumVectorized(ReadOnlySpan<float> prices)
    {
        if (Vector.IsHardwareAccelerated && prices.Length >= Vector<float>.Count)
        {
            var sumVector = Vector<float>.Zero;
            int i = 0;
            int count = prices.Length;
            int step = Vector<float>.Count;

            for (; i <= count - step; i += step)
            {
                var v = new Vector<float>(prices.Slice(i, step));
                sumVector += v;
            }

            float sum = Vector.Dot(sumVector, Vector<float>.One);

            // Process remaining elements
            for (; i < count; i++)
            {
                sum += prices[i];
            }

            return sum;
        }

        // Fallback for non-supported hardware
        float total = 0;
        foreach (var p in prices) total += p;
        return total;
    }

    /// <summary>
    /// Calculates Simple Moving Average (SMA) for multiple windows simultaneously using SIMD.
    /// </summary>
    public static void CalculateBatchSMA(ReadOnlySpan<float> prices, int windowSize, Span<float> results)
    {
        if (prices.Length < windowSize) return;

        float windowSum = SumVectorized(prices[..windowSize]);
        results[0] = windowSum / windowSize;

        for (int i = 1; i <= prices.Length - windowSize; i++)
        {
            windowSum += prices[i + windowSize - 1] - prices[i - 1];
            results[i] = windowSum / windowSize;
        }
    }
}
