using System;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Closed-form OLS regression of equity vs time-index. Decomposes a realised
/// equity curve into <c>trend(t) = slope·t + intercept</c> and per-bar
/// residuals so the dashboard can plot the smooth drift line on top of the
/// realised curve and quantify how much the strategy compounded vs wandered.
/// </summary>
/// <remarks>
/// Why bar-index (not calendar time): for irregularly-spaced bars (e.g. crypto
/// 4h with occasional outage gaps) the bar-index keeps the slope interpretable
/// as "$ added per accepted bar". A calendar-time slope would blend in the
/// no-trade gaps which isn't what the operator wants to see.
///
/// R² interpretation reminders:
/// <list type="bullet">
/// <item>R² &gt; 0.95 = nearly-monotonic compounding (rare, suspicious for short
/// samples; expected for very mature strategies on long history).</item>
/// <item>0.7 &lt; R² &lt; 0.95 = healthy edge with some path noise.</item>
/// <item>0.3 &lt; R² &lt; 0.7 = noisy realised curve with directional drift.</item>
/// <item>R² &lt; 0.3 = mostly random walk; slope might be illusory.</item>
/// </list>
/// </remarks>
public sealed class LinearRegressionDecomposer
{
    /// <summary>Decompose the equity curve into trend + residuals.</summary>
    /// <param name="equityCurve">Per-bar realised equity (USD). Length N.</param>
    public LinearRegressionResult Decompose(decimal[] equityCurve)
    {
        if (equityCurve is null) throw new ArgumentNullException(nameof(equityCurve));
        int n = equityCurve.Length;
        if (n < 2)
        {
            // Degenerate: <2 points has no regression. Return a flat trend at the
            // single value (or zeros if empty) so the chart can still render.
            decimal flat = n == 1 ? equityCurve[0] : 0m;
            return new LinearRegressionResult(
                SlopeUsdPerBar:  0.0,
                InterceptUsd:    (double)flat,
                RSquared:        0.0,
                ResidualStdUsd:  0.0,
                TrendUsd:        n == 1 ? new[] { flat } : Array.Empty<decimal>(),
                ResidualsUsd:    n == 1 ? new[] { 0m } : Array.Empty<decimal>(),
                NObservations:   n);
        }

        // OLS closed form. Using doubles for numerical stability — equity values
        // are decimals on input/output, but the sums-of-squares and means work
        // better in IEEE 754 (decimal multiplication overflows on long curves).
        // Acceptable precision loss: equity curves are O($10^4-10^5), bars O(10^4),
        // so Σ x² fits well under double range without precision issues.
        double xMean = (n - 1) / 2.0;
        double yMean = 0.0;
        for (int i = 0; i < n; i++) yMean += (double)equityCurve[i];
        yMean /= n;

        double covXY = 0.0;
        double varX  = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = i - xMean;
            double dy = (double)equityCurve[i] - yMean;
            covXY += dx * dy;
            varX  += dx * dx;
        }

        double slope = varX == 0.0 ? 0.0 : covXY / varX;
        double intercept = yMean - slope * xMean;

        // Trend + residuals + SSres for R² + std.
        var trend = new decimal[n];
        var residuals = new decimal[n];
        double ssRes = 0.0;
        double ssTot = 0.0;
        for (int i = 0; i < n; i++)
        {
            double t = slope * i + intercept;
            trend[i] = (decimal)t;
            double r = (double)equityCurve[i] - t;
            residuals[i] = (decimal)r;
            ssRes += r * r;
            double dy = (double)equityCurve[i] - yMean;
            ssTot += dy * dy;
        }

        double rSquared = ssTot == 0.0 ? 0.0 : Math.Max(0.0, 1.0 - ssRes / ssTot);
        double residStd = Math.Sqrt(ssRes / Math.Max(n - 2, 1));

        return new LinearRegressionResult(
            SlopeUsdPerBar:  slope,
            InterceptUsd:    intercept,
            RSquared:        rSquared,
            ResidualStdUsd:  residStd,
            TrendUsd:        trend,
            ResidualsUsd:    residuals,
            NObservations:   n);
    }
}
