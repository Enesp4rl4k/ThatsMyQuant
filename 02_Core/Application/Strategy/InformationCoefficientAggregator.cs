using System;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Information Coefficient (IC) signal aggregator. Combines multiple raw alpha
/// signals into a single composite score using each signal's empirical IC
/// (rank correlation between signal and subsequent return) as the weight.
/// Strong signals contribute proportionally; weak/decaying signals are
/// automatically de-weighted; uncorrelated noise is filtered out.
///
/// References:
///   Grinold &amp; Kahn (2000), "Active Portfolio Management", Ch. 11 — IC and IR.
///   Lopez de Prado (2018), "Advances in Financial Machine Learning", Ch. 8.
///   Qian, Hua, Sorensen (2007), "Quantitative Equity Portfolio Management" —
///     standard reference for breadth-via-IC composite construction.
///
/// Math:
///   For each signal i with rank correlation IC_i to next-period returns:
///     composite = Σ_i (sign(IC_i) · |IC_i|^p · standardize(signal_i)) / Σ_i |IC_i|^p
///
///   p ∈ [0,2]:
///     p = 0 → equal-weighted average (ignores IC).
///     p = 1 → linear weighting by IC.
///     p = 2 → quadratic — penalizes weak signals harder.
///
/// Why this matters for the upgrade plan:
///   Phase K9 — the system will have 5-8 active signals after the plan
///   completes (regime gate, OFI, CVD, funding flip, stop-hunt, BTC.D
///   overlay, PCA residuals). Each has its own backtest-derived IC. Manually
///   weighting them is error-prone; IC-weighted aggregation is the
///   industry-standard solution and adapts as ICs decay over time.
///
///   The composite score is then thresholded for entries (e.g., enter when
///   |composite| &gt; 1σ) and used as confidence multiplier for sizing.
/// </summary>
public static class InformationCoefficientAggregator
{
    /// <summary>One alpha signal's metadata bundle.</summary>
    /// <param name="Name">Friendly name for telemetry / digests.</param>
    /// <param name="Value">Latest signal value (caller standardizes externally OR via this aggregator).</param>
    /// <param name="HistoricalIc">Rank correlation between signal and subsequent return,
    /// computed offline on backtest data. Updated periodically (e.g. weekly).</param>
    public sealed record SignalEntry(string Name, double Value, double HistoricalIc);

    /// <summary>Aggregation result.</summary>
    /// <param name="CompositeScore">Combined signal in standardized (z-score) units.</param>
    /// <param name="EffectiveWeight">Sum of |IC|^p — useful diagnostic; low value means weak overall edge.</param>
    /// <param name="ContributingSignals">Per-signal contributions to the composite for telemetry.</param>
    public sealed record AggregationResult(
        double CompositeScore,
        double EffectiveWeight,
        IReadOnlyList<SignalContribution> ContributingSignals);

    public sealed record SignalContribution(
        string Name,
        double StandardizedValue,
        double WeightApplied,
        double Contribution);

    /// <summary>
    /// Aggregate multiple already-standardized signals using IC-weighted scoring.
    /// </summary>
    /// <param name="signals">Signal entries with current values and historical ICs. Each Value
    /// is assumed to already be in z-score / standardized form (mean 0, std 1).</param>
    /// <param name="exponent">p — weight exponent. 1.0 = linear, 2.0 = quadratic emphasis on
    /// strong signals, 0.0 = equal weight. Default 1.0.</param>
    /// <param name="minAbsIc">Signals below this absolute-IC are dropped entirely (treated as
    /// noise). Default 0.02.</param>
    public static AggregationResult Aggregate(
        IEnumerable<SignalEntry> signals,
        double exponent = 1.0,
        double minAbsIc = 0.02)
    {
        ArgumentNullException.ThrowIfNull(signals);
        if (exponent < 0)
            throw new ArgumentOutOfRangeException(nameof(exponent), "Must be non-negative.");
        if (minAbsIc < 0)
            throw new ArgumentOutOfRangeException(nameof(minAbsIc), "Must be non-negative.");

        var contributions = new List<SignalContribution>();
        double weightedSum = 0.0;
        double totalWeight = 0.0;
        foreach (var s in signals)
        {
            double absIc = Math.Abs(s.HistoricalIc);
            if (absIc < minAbsIc)
            {
                contributions.Add(new SignalContribution(s.Name, s.Value, 0.0, 0.0));
                continue;
            }
            double w = Math.Pow(absIc, exponent);
            double signedW = w * Math.Sign(s.HistoricalIc);
            double contrib = signedW * s.Value;
            weightedSum += contrib;
            totalWeight += w;
            contributions.Add(new SignalContribution(s.Name, s.Value, signedW, contrib));
        }

        double composite = totalWeight > 0 ? weightedSum / totalWeight : 0.0;

        return new AggregationResult(
            CompositeScore: composite,
            EffectiveWeight: totalWeight,
            ContributingSignals: contributions);
    }

    /// <summary>
    /// Compute the rank correlation (Spearman's ρ) between two paired series.
    /// Used offline to estimate the IC of a candidate signal vs subsequent returns.
    /// </summary>
    public static double SpearmanRank(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Count != y.Count)
            throw new ArgumentException("Series must be same length.");
        int n = x.Count;
        if (n < 2) return 0.0;

        var rankX = ComputeRanks(x);
        var rankY = ComputeRanks(y);

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double d = rankX[i] - rankY[i];
            sum += d * d;
        }
        // Spearman: ρ = 1 − 6·Σd² / (n·(n²−1))
        return 1.0 - 6.0 * sum / (n * (double)(n * n - 1));
    }

    /// <summary>
    /// Standardize a series in place: subtract mean, divide by stddev.
    /// </summary>
    public static double[] Standardize(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int n = values.Count;
        if (n == 0) return Array.Empty<double>();

        double mean = 0;
        for (int i = 0; i < n; i++) mean += values[i];
        mean /= n;

        double sq = 0;
        for (int i = 0; i < n; i++) { double d = values[i] - mean; sq += d * d; }
        double std = Math.Sqrt(sq / Math.Max(1, n - 1));

        var standardized = new double[n];
        if (std > 0)
        {
            for (int i = 0; i < n; i++) standardized[i] = (values[i] - mean) / std;
        }
        return standardized;
    }

    private static double[] ComputeRanks(IReadOnlyList<double> values)
    {
        int n = values.Count;
        var indexed = values
            .Select((v, i) => (Value: v, Index: i))
            .OrderBy(p => p.Value)
            .ToArray();

        var ranks = new double[n];
        int k = 0;
        while (k < n)
        {
            int j = k;
            // Group ties.
            while (j + 1 < n && indexed[j + 1].Value == indexed[k].Value) j++;
            double avgRank = (k + j) / 2.0 + 1.0;  // 1-based rank, averaged over ties
            for (int p = k; p <= j; p++) ranks[indexed[p].Index] = avgRank;
            k = j + 1;
        }
        return ranks;
    }
}
