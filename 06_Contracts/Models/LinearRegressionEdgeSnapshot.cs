namespace ThatsMyQuant.Contracts.Models;

/// <summary>
/// Compact rolling linear-regression context used by scoring and execution gates.
/// Slope is reported both raw (price/bar) and normalized against ATR so strength
/// remains comparable across different volatility regimes.
/// </summary>
public sealed record LinearRegressionEdgeSnapshot
{
    public int Lookback { get; init; }
    public decimal Slope { get; init; }
    public decimal NormalizedSlope { get; init; }
    public decimal RegressionValue { get; init; }
    public decimal Residual { get; init; }
    public double SigmaDistance { get; init; }
    public double StandardError { get; init; }
    public double RSquared { get; init; }
    public bool IsOutsideOnePointFiveSigma { get; init; }
    public bool IsOutsideTwoSigma { get; init; }
    public bool IsOverextendedAbove { get; init; }
}
