using System;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Bar-by-bar Monte Carlo bootstrap over an equity curve. Resamples per-bar
/// returns with replacement to build a percentile envelope (a "fan chart") of
/// alternative equity paths the strategy could have taken given the same
/// return distribution.
/// </summary>
/// <remarks>
/// <para><b>Why not the existing <see cref="MonteCarloBootstrapService"/>?</b>
/// That service resamples whole TRADES and returns final-equity / Sharpe /
/// MaxDD confidence intervals — useful for "is my strategy distinguishable
/// from noise?" but not for visual operator review of the path itself.</para>
///
/// <para>This service instead works at the BAR level and returns the percentile
/// envelope at every time step, suitable for rendering as a stacked-area
/// "fan" overlaid on the realised curve. The realised path landing inside
/// the 25–75 band over most of its length is a strong "this looks plausible"
/// signal; landing outside the 5–95 band repeatedly is a "this is suspiciously
/// good" signal that begs for an OOS check.</para>
///
/// <para>Determinism: pass a fixed <paramref name="randomSeed"/> for reproducible
/// fan charts in the dashboard. The default (null) uses a fresh seed per call.</para>
///
/// <para>Algorithmic notes: this is a <i>non-parametric</i> bootstrap. We do NOT
/// assume returns are Gaussian or i.i.d. — we just resample the empirical
/// per-bar returns with replacement. This destroys serial correlation by
/// design (each path is a new random ordering), which is fine for the
/// "alternative-realisation envelope" question but underestimates drawdown
/// magnitude for strategies with strong trend autocorrelation. A block
/// bootstrap is a future enhancement (block size = autocorrelation window).</para>
/// </remarks>
public sealed class MonteCarloFanService
{
    /// <summary>Compute a per-bar percentile fan from an equity curve.</summary>
    /// <param name="dates">Per-bar UTC timestamps. Length = N.</param>
    /// <param name="equityCurve">Per-bar realised equity, in USD. Length = N.
    /// Must start at the strategy's initial capital.</param>
    /// <param name="iterations">Number of bootstrap paths. 1000 is a good default —
    /// gives stable 5/95 percentiles without dominating CPU. Bumping past 5000
    /// has diminishing returns.</param>
    /// <param name="randomSeed">Fixed seed for deterministic output. Default null = fresh seed.</param>
    public MonteCarloFanResult Compute(
        DateTime[] dates,
        decimal[] equityCurve,
        int iterations = 1000,
        int? randomSeed = null)
    {
        if (dates is null) throw new ArgumentNullException(nameof(dates));
        if (equityCurve is null) throw new ArgumentNullException(nameof(equityCurve));
        if (dates.Length != equityCurve.Length)
            throw new ArgumentException(
                $"dates ({dates.Length}) and equityCurve ({equityCurve.Length}) must align.",
                nameof(equityCurve));
        if (iterations < 50)
            throw new ArgumentOutOfRangeException(
                nameof(iterations), iterations, "Iterations must be >= 50 for stable percentiles.");

        int n = equityCurve.Length;

        // Edge case: a flat or single-point curve has no return distribution to bootstrap.
        // Return a degenerate fan that exactly matches the realised curve at all percentiles
        // so the dashboard renders a flat line instead of an empty chart.
        if (n < 2)
        {
            return DegenerateFan(dates, equityCurve, iterations, randomSeed);
        }

        // Per-bar arithmetic returns: r_i = (equity_i - equity_{i-1}) / equity_{i-1}.
        // We resample these (not the equity values directly) so the fan paths start
        // from the same initial capital and compound the same return distribution.
        double[] returns = new double[n - 1];
        decimal startEquity = equityCurve[0];
        for (int i = 1; i < n; i++)
        {
            decimal prev = equityCurve[i - 1];
            if (prev == 0m)
            {
                // Strategy went to zero — bootstrap is undefined past this point. Truncate.
                Array.Resize(ref returns, i - 1);
                break;
            }
            returns[i - 1] = (double)((equityCurve[i] - prev) / prev);
        }

        if (returns.Length == 0)
            return DegenerateFan(dates, equityCurve, iterations, randomSeed);

        // Run the bootstrap. Each path is an array of length n (start equity at index 0,
        // then n-1 compounded steps). We then transpose to per-bar columns for percentile.
        var rng = randomSeed is null ? new Random() : new Random(randomSeed.Value);
        // Pre-compute per-bar columns to avoid materialising a 1000×N matrix at full
        // precision: we only need the per-step values to feed the percentile sort.
        // Memory: iterations × n decimals = 1000 × ~250 = 250k decimals (~4 MB). Fine.
        decimal[,] paths = new decimal[iterations, n];
        for (int it = 0; it < iterations; it++)
        {
            paths[it, 0] = startEquity;
            decimal eq = startEquity;
            for (int b = 1; b < n; b++)
            {
                int sampleIdx = rng.Next(returns.Length);
                eq = eq * (1m + (decimal)returns[sampleIdx]);
                paths[it, b] = eq;
            }
        }

        // Per-bar percentile extraction. Sort iterations[i] for each bar and pluck the
        // 5/25/50/75/95 cells. Using a fresh array each bar to keep the sort cheap.
        var p05 = new decimal[n];
        var p25 = new decimal[n];
        var p50 = new decimal[n];
        var p75 = new decimal[n];
        var p95 = new decimal[n];
        var col = new decimal[iterations];
        for (int b = 0; b < n; b++)
        {
            for (int it = 0; it < iterations; it++) col[it] = paths[it, b];
            Array.Sort(col);
            p05[b] = Percentile(col, 0.05);
            p25[b] = Percentile(col, 0.25);
            p50[b] = Percentile(col, 0.50);
            p75[b] = Percentile(col, 0.75);
            p95[b] = Percentile(col, 0.95);
        }

        return new MonteCarloFanResult(
            Dates: dates,
            Percentile05: p05,
            Percentile25: p25,
            Percentile50: p50,
            Percentile75: p75,
            Percentile95: p95,
            RealizedEquity: equityCurve,
            Iterations: iterations,
            RandomSeed: randomSeed);
    }

    private static decimal Percentile(decimal[] sorted, double p)
    {
        // Linear-interpolation percentile (numpy default, type 7). Same convention as
        // pandas Series.quantile(p, interpolation='linear') so dashboard matches Python research.
        int n = sorted.Length;
        if (n == 0) return 0m;
        if (n == 1) return sorted[0];
        double idx = p * (n - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        decimal w = (decimal)(idx - lo);
        return sorted[lo] * (1m - w) + sorted[hi] * w;
    }

    private static MonteCarloFanResult DegenerateFan(
        DateTime[] dates, decimal[] equityCurve, int iterations, int? randomSeed)
    {
        // n == 0 or 1, OR strategy went to zero on the very first bar.
        // Render a flat line at every percentile so the chart still draws.
        return new MonteCarloFanResult(
            Dates: dates,
            Percentile05: (decimal[])equityCurve.Clone(),
            Percentile25: (decimal[])equityCurve.Clone(),
            Percentile50: (decimal[])equityCurve.Clone(),
            Percentile75: (decimal[])equityCurve.Clone(),
            Percentile95: (decimal[])equityCurve.Clone(),
            RealizedEquity: (decimal[])equityCurve.Clone(),
            Iterations: iterations,
            RandomSeed: randomSeed);
    }
}
