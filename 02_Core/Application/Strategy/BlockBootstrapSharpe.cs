using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Block-bootstrap Sharpe-ratio confidence intervals — Politis &amp; Romano (1994)
/// stationary bootstrap. Naive bootstrap on autocorrelated return series
/// destroys the dependence structure and produces wildly optimistic CIs.
/// Block bootstrap resamples contiguous blocks of length L, preserving local
/// autocorrelation. Stationary version randomizes block length to avoid
/// boundary artifacts.
///
/// Why this matters for the upgrade plan:
///   Backtest Sharpe is a point estimate. Two strategies with the same Sharpe
///   3.5 but different sample sizes / regimes have very different uncertainty.
///   The 95% CI tells us whether 3.5 is "definitely above 2" or "could be 1.8
///   to 5.2" — critical for the paper-restart go/no-go decision.
///
/// Algorithm:
///   1. Pick block length L (default = round(T^{1/3}), Politis-Romano optimal rate).
///   2. Repeat B times:
///        a. Sample blocks of geometric-distributed length (mean L) with replacement
///           from the original series.
///        b. Concatenate to form a synthetic series of length T.
///        c. Compute Sharpe on the synthetic series.
///   3. Return percentile bounds (e.g. 2.5%, 97.5%).
/// </summary>
public static class BlockBootstrapSharpe
{
    public sealed record SharpeCi(
        double PointEstimate,
        double LowerBound,
        double UpperBound,
        double Mean,
        double StandardError);

    /// <summary>
    /// Compute a confidence interval for the Sharpe ratio of a return series.
    /// </summary>
    /// <param name="returns">Per-period returns.</param>
    /// <param name="numBootstrap">Number of bootstrap replications. Default 1000.</param>
    /// <param name="lowerPercentile">Lower CI percentile (0-1). Default 0.025.</param>
    /// <param name="upperPercentile">Upper CI percentile (0-1). Default 0.975.</param>
    /// <param name="meanBlockLength">Mean block length. Default = ceil(T^{1/3}).</param>
    /// <param name="seed">RNG seed for deterministic tests.</param>
    public static SharpeCi Compute(
        IReadOnlyList<double> returns,
        int numBootstrap = 1000,
        double lowerPercentile = 0.025,
        double upperPercentile = 0.975,
        int? meanBlockLength = null,
        int seed = 42)
    {
        ArgumentNullException.ThrowIfNull(returns);
        if (returns.Count < 10)
            throw new ArgumentException("Need at least 10 observations.", nameof(returns));
        if (numBootstrap < 100)
            throw new ArgumentOutOfRangeException(nameof(numBootstrap));
        if (lowerPercentile <= 0 || lowerPercentile >= upperPercentile || upperPercentile >= 1)
            throw new ArgumentException("Require 0 < lower < upper < 1.");

        int t = returns.Count;
        int meanBlock = meanBlockLength ?? Math.Max(2, (int)Math.Ceiling(Math.Pow(t, 1.0 / 3.0)));

        double pointEstimate = Sharpe(returns);
        var rng = new Random(seed);
        double pBlockEnd = 1.0 / meanBlock;  // geometric prob each draw ends the block

        var sharpes = new double[numBootstrap];
        var synthetic = new double[t];

        for (int b = 0; b < numBootstrap; b++)
        {
            int idx = rng.Next(t);
            for (int j = 0; j < t; j++)
            {
                synthetic[j] = returns[idx];
                idx++;
                // Geometric block-end: with probability pBlockEnd start a new block.
                if (idx >= t || rng.NextDouble() < pBlockEnd)
                {
                    idx = rng.Next(t);
                }
            }
            sharpes[b] = Sharpe(synthetic);
        }

        Array.Sort(sharpes);
        double lower = Percentile(sharpes, lowerPercentile);
        double upper = Percentile(sharpes, upperPercentile);
        double mean = 0;
        for (int i = 0; i < numBootstrap; i++) mean += sharpes[i];
        mean /= numBootstrap;
        double sq = 0;
        for (int i = 0; i < numBootstrap; i++) { double d = sharpes[i] - mean; sq += d * d; }
        double se = Math.Sqrt(sq / Math.Max(1, numBootstrap - 1));

        return new SharpeCi(
            PointEstimate: pointEstimate,
            LowerBound: lower,
            UpperBound: upper,
            Mean: mean,
            StandardError: se);
    }

    private static double Sharpe(IReadOnlyList<double> returns)
    {
        int n = returns.Count;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += returns[i];
        mean /= n;
        double sq = 0;
        for (int i = 0; i < n; i++) { double d = returns[i] - mean; sq += d * d; }
        double std = Math.Sqrt(sq / Math.Max(1, n - 1));
        return std > 0 ? mean / std : 0.0;
    }

    private static double Percentile(double[] sortedSharpes, double p)
    {
        int n = sortedSharpes.Length;
        double pos = p * (n - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(n - 1, lo + 1);
        double frac = pos - lo;
        return sortedSharpes[lo] * (1 - frac) + sortedSharpes[hi] * frac;
    }
}
