using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Result DTOs for the Backtest Lab analysis layer. Three independent diagnostic
/// views over a single equity curve, designed to be rendered side-by-side on the
/// React <c>BacktestLabPage</c>.
/// </summary>
/// <remarks>
/// All three views answer different questions:
/// <list type="bullet">
/// <item><b>MC Fan</b>: "Was this performance lucky? What's the envelope of plausible
/// alternative paths?"</item>
/// <item><b>Linear Regression Decomposition</b>: "How much is steady drift vs noise?
/// Does the strategy compound or wander?"</item>
/// <item><b>Shannon Entropy Trajectory</b>: "Is the return distribution stable, or
/// does it concentrate/disperse over time? Regime shifts show up here before
/// drawdown does."</item>
/// </list>
/// Together they let an operator distinguish a real edge (steady regression line +
/// tight MC fan + stable entropy) from a flukey one (noisy regression + wide fan +
/// entropy spikes around the lucky stretch).
/// </remarks>
public sealed record MonteCarloFanResult(
    DateTime[] Dates,
    /// <summary>5th percentile equity at each bar — the "you got unlucky" path.</summary>
    decimal[] Percentile05,
    decimal[] Percentile25,
    /// <summary>Median path — the most-likely realised equity given the return distribution.</summary>
    decimal[] Percentile50,
    decimal[] Percentile75,
    /// <summary>95th percentile — the "you got lucky" path.</summary>
    decimal[] Percentile95,
    /// <summary>Actual realised equity, kept here so the chart can overlay it on the fan.</summary>
    decimal[] RealizedEquity,
    int    Iterations,
    int?   RandomSeed);

public sealed record LinearRegressionResult(
    /// <summary>Slope of equity vs bar-index. Units: USD per bar.</summary>
    double SlopeUsdPerBar,
    /// <summary>Intercept (USD at bar 0).</summary>
    double InterceptUsd,
    /// <summary>R² in [0,1]. High R² = monotonic compounding; low = random walk around mean.</summary>
    double RSquared,
    /// <summary>Standard deviation of residuals (USD). High = noisy realised path around the trend.</summary>
    double ResidualStdUsd,
    /// <summary>Predicted equity at each bar — the smooth trend line.</summary>
    decimal[] TrendUsd,
    /// <summary>Realised − Trend at each bar — the noise the strategy added on top of pure drift.</summary>
    decimal[] ResidualsUsd,
    int NObservations);

public sealed record ShannonEntropyResult(
    /// <summary>Bar timestamps aligned 1:1 with <see cref="Entropy"/>.</summary>
    DateTime[] Dates,
    /// <summary>Rolling Shannon entropy in nats (natural-log units). NaN until window is full.</summary>
    double[] Entropy,
    int WindowBars,
    int Bins,
    /// <summary>Theoretical max entropy = ln(Bins). Use to normalise (0..1) on the client if desired.</summary>
    double MaxEntropyNats);
