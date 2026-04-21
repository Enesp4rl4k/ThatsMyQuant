using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Candle-derived order-flow approximation. True CVD needs tick data, but a reasonable
/// proxy is computable from OHLCV: signed volume by close position inside the bar range.
///
///   signedDelta = volume * ((close - open) / (high - low))   when high > low
///
/// This captures "aggressive" positioning: a bar that closes near its high with large
/// volume suggests net buying pressure; closing near its low on large volume suggests
/// net selling. Summed over N bars, the slope of this CVD is a momentum confirmation.
///
/// Gate rule (entry filter):
///   - LONG requires cvdSlope > +threshold
///   - SHORT requires cvdSlope < -threshold
/// threshold scales with the CVD historical stdev so it is volatility-adaptive.
/// </summary>
public static class OrderFlowSignalService
{
    public const int CvdLookbackBars = 20;
    public const int StdevLookbackBars = 60;
    public const decimal SlopeThresholdStdevs = 0.35m;

    public readonly record struct FlowGateResult(bool Pass, string? RejectReason, decimal CvdSlope, decimal Threshold);

    public static FlowGateResult Evaluate(IReadOnlyList<Candle> window, TradeSide side)
    {
        if (window is null || window.Count < StdevLookbackBars + 2)
            return new FlowGateResult(true, null, 0m, 0m);

        // Rolling CVD series over the last StdevLookback bars.
        int start = window.Count - StdevLookbackBars;
        var cvd = new decimal[StdevLookbackBars];
        decimal running = 0m;
        for (int i = 0; i < StdevLookbackBars; i++)
        {
            running += SignedDelta(window[start + i]);
            cvd[i] = running;
        }

        // Slope = last value minus value CvdLookbackBars ago.
        int lookIdx = Math.Max(0, StdevLookbackBars - CvdLookbackBars);
        decimal slope = cvd[^1] - cvd[lookIdx];

        // Stdev of slope-interval changes — used to scale threshold.
        var diffs = new List<decimal>(StdevLookbackBars);
        for (int i = CvdLookbackBars; i < StdevLookbackBars; i++)
            diffs.Add(cvd[i] - cvd[i - CvdLookbackBars]);
        decimal threshold = 0m;
        if (diffs.Count > 0)
        {
            decimal mean = diffs.Sum() / diffs.Count;
            decimal variance = diffs.Sum(d => (d - mean) * (d - mean)) / diffs.Count;
            decimal stdev = (decimal)Math.Sqrt((double)variance);
            threshold = stdev * SlopeThresholdStdevs;
        }

        if (threshold <= 0m)
            return new FlowGateResult(true, null, slope, 0m);

        if (side == TradeSide.Buy && slope < threshold)
            return new FlowGateResult(false, "OrderFlowAgainstLong", slope, threshold);
        if (side == TradeSide.Sell && slope > -threshold)
            return new FlowGateResult(false, "OrderFlowAgainstShort", slope, threshold);

        return new FlowGateResult(true, null, slope, threshold);
    }

    private static decimal SignedDelta(Candle c)
    {
        decimal range = c.High - c.Low;
        if (range <= 0m || c.Volume <= 0m) return 0m;
        decimal position = (c.Close - c.Open) / range;
        return c.Volume * position;
    }
}
