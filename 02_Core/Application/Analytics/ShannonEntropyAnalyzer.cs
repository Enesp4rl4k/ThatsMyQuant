using System;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Rolling Shannon entropy of bar-return distribution. For a window of size
/// <c>W</c>, computes <c>H = -Σ p_i ln(p_i)</c> over <c>K</c> equal-width bins
/// of the in-window returns, sliding the window one bar at a time.
/// </summary>
/// <remarks>
/// <para><b>Why operators care:</b> entropy is a regime-shift early-warning signal.
/// When return distribution sharpens (concentrates around mean) entropy drops —
/// often this happens BEFORE a strategy enters a drawdown ("the easy money is
/// being squeezed out"). When entropy spikes the regime is volatile, fat-tailed
/// returns are dominating and noise-fitting risk is highest.</para>
///
/// <para>Compared to rolling volatility (which only measures dispersion around mean),
/// entropy captures shape: a bimodal distribution with the same standard deviation
/// as a Gaussian has higher entropy. For trading, that translates to "the market
/// is in two modes" insight that simple stdev hides.</para>
///
/// <para>Default <c>W=30</c> is roughly 1 day of 4h bars or 1.25 days of 1h bars —
/// the shortest window where the histogram is still non-degenerate. Default
/// <c>K=10</c> bins balances resolution against the small-sample bias (you want
/// at least 3 bars per bin on average; 30/10 = 3 ✓).</para>
///
/// <para>Output convention: the first <c>W-1</c> entropy values are <see cref="double.NaN"/>
/// (window not yet full). The dashboard skips these on the chart.</para>
/// </remarks>
public sealed class ShannonEntropyAnalyzer
{
    /// <summary>Compute the rolling-entropy trajectory.</summary>
    /// <param name="dates">Per-bar UTC timestamps. Length N.</param>
    /// <param name="equityCurve">Per-bar equity (USD). Length N. Returns derived as
    /// arithmetic differences of equity (consistent with MC fan service).</param>
    /// <param name="window">Bars per rolling window. Default 30. Must be >= 5.</param>
    /// <param name="bins">Histogram bins per window. Default 10.</param>
    public ShannonEntropyResult Analyse(
        DateTime[] dates,
        decimal[] equityCurve,
        int window = 30,
        int bins = 10)
    {
        if (dates is null) throw new ArgumentNullException(nameof(dates));
        if (equityCurve is null) throw new ArgumentNullException(nameof(equityCurve));
        if (dates.Length != equityCurve.Length)
            throw new ArgumentException(
                $"dates ({dates.Length}) and equityCurve ({equityCurve.Length}) must align.");
        if (window < 5)
            throw new ArgumentOutOfRangeException(
                nameof(window), window, "Window must be >= 5 for a non-degenerate histogram.");
        if (bins < 2)
            throw new ArgumentOutOfRangeException(
                nameof(bins), bins, "Bins must be >= 2.");

        int n = equityCurve.Length;
        var entropy = new double[n];
        for (int i = 0; i < n; i++) entropy[i] = double.NaN;

        // Per-bar returns: r_i = (eq_i - eq_{i-1}) / eq_{i-1}. Length n-1, indexed
        // from i=1 in the equity timeline. We align entropy[i] to the END of the
        // window covering returns[i-window+1 .. i].
        if (n < window + 1)
        {
            // Not enough bars to fill a single window — return all-NaN. Operator
            // sees an empty entropy line but the rest of the page still works.
            return new ShannonEntropyResult(
                Dates:           dates,
                Entropy:         entropy,
                WindowBars:      window,
                Bins:            bins,
                MaxEntropyNats:  Math.Log(bins));
        }

        var returns = new double[n - 1];
        for (int i = 1; i < n; i++)
        {
            decimal prev = equityCurve[i - 1];
            returns[i - 1] = prev == 0m ? 0.0 : (double)((equityCurve[i] - prev) / prev);
        }

        // Slide the window. At equity bar `i`, the window covers returns
        // indices [i-window .. i-1] inclusive — i.e. returns[i-window]..returns[i-1].
        var win = new double[window];
        var counts = new int[bins];
        for (int i = window; i < n; i++)
        {
            for (int k = 0; k < window; k++) win[k] = returns[i - window + k];

            double min = win[0], max = win[0];
            for (int k = 1; k < window; k++)
            {
                if (win[k] < min) min = win[k];
                if (win[k] > max) max = win[k];
            }

            // Degenerate window: all returns identical → entropy = 0 (perfectly concentrated).
            if (max == min)
            {
                entropy[i] = 0.0;
                continue;
            }

            // Bin the window. Use [min, max] divided into `bins` equal-width bins.
            double width = (max - min) / bins;
            Array.Clear(counts, 0, bins);
            for (int k = 0; k < window; k++)
            {
                int idx = (int)((win[k] - min) / width);
                if (idx >= bins) idx = bins - 1; // boundary case for max-valued returns
                counts[idx]++;
            }

            // H = -Σ p_i ln(p_i). 0·ln(0) := 0 by convention (skip empty bins).
            double h = 0.0;
            double inv = 1.0 / window;
            for (int b = 0; b < bins; b++)
            {
                if (counts[b] == 0) continue;
                double p = counts[b] * inv;
                h -= p * Math.Log(p);
            }
            entropy[i] = h;
        }

        return new ShannonEntropyResult(
            Dates:           dates,
            Entropy:         entropy,
            WindowBars:      window,
            Bins:            bins,
            MaxEntropyNats:  Math.Log(bins));
    }
}
