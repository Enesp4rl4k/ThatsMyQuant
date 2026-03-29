using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThatsMyQuant.Infrastructure.Services.Strategy;

/// <summary>
/// Enhanced statistical arbitrage strategy using 4-factor confluence:
///   1. Z-score deviation — price must be ≥ 2σ from rolling mean
///   2. Coefficient of variation — ensures meaningful relative dispersion
///   3. Half-life estimation — Ornstein-Uhlenbeck mean-reversion speed
///   4. Cointegration pre-check — ADF on the price residual
///
/// Scoring: base 5 per confirmed factor. Half-life and ADF add bonuses.
/// Minimum 3/4 factors required for a signal.
/// </summary>
public sealed class StatisticalArbitrageAgent : IStrategyAgent
{
    private readonly ILogger<StatisticalArbitrageAgent> _logger;
    private readonly StatArbOptions _opts;

    public string StrategyTag => "StatArb";
    public int SwarmWeight => _opts.SwarmWeight;

    public StatisticalArbitrageAgent(ILogger<StatisticalArbitrageAgent> logger, IOptions<StatArbOptions>? options = null)
    {
        _logger = logger;
        _opts = options?.Value ?? new StatArbOptions();
    }

    public bool CanHandle(Signal signal, MarketRegime regime)
        => regime is MarketRegime.Ranging;

    public Task<StrategyVote> EvaluateAsync(
        Signal signal,
        PatternSnapshot patterns,
        FoundationSnapshot foundation,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count < _opts.LookbackBars)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var closes = candles.TakeLast(_opts.LookbackBars).Select(c => (double)c.Close).ToArray();
        var mean = closes.Average();
        var variance = closes.Select(c => (c - mean) * (c - mean)).Average();
        var stdDev = Math.Sqrt(variance);

        // ── Factor 1: Z-score deviation ──
        if (stdDev < 1e-10 || mean == 0)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        var zScore = ((double)signal.Price - mean) / stdDev;
        bool hasDeviation = Math.Abs(zScore) >= _opts.EntryZScore;

        if (!hasDeviation)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        // ── Factor 2: Coefficient of Variation ──
        var cv = stdDev / Math.Abs(mean);
        bool hasMeaningfulDispersion = cv >= _opts.MinCoefficientOfVariation;

        // ── Factor 3: Half-life of mean reversion ──
        var halfLife = EstimateHalfLife(closes);
        bool hasGoodHalfLife = halfLife > 0 && halfLife <= _opts.MaxHalfLife;
        bool hasFastHalfLife = halfLife > 0 && halfLife <= _opts.FastHalfLife;

        // ── Factor 4: Cointegration / stationarity (ADF test) ──
        var adfStat = ComputeAdfStatistic(closes);
        bool isStationary = adfStat < _opts.AdfCriticalValue;

        // ── Confluence scoring ──
        int confirmedFactors = 1; // z-score is the base factor
        var reasons = new List<string>(5) { $"z={zScore:F2}" };

        if (hasMeaningfulDispersion)
        {
            confirmedFactors++;
            reasons.Add($"CV={cv:P2}");
        }
        else
        {
            reasons.Add($"CV={cv:P2} low");
        }

        if (hasGoodHalfLife)
        {
            confirmedFactors++;
            reasons.Add($"HL={halfLife:F1} bars");
        }
        else
        {
            reasons.Add(halfLife <= 0 ? "HL=∞ (no reversion)" : $"HL={halfLife:F1} slow");
        }

        if (isStationary)
        {
            confirmedFactors++;
            reasons.Add($"ADF={adfStat:F2} stationary");
        }
        else
        {
            reasons.Add($"ADF={adfStat:F2} non-stationary");
        }

        // Need at least 3 of 4 factors
        if (confirmedFactors < 3)
            return Task.FromResult(StrategyVote.Abstain(StrategyTag));

        float confidence = confirmedFactors * _opts.BaseConfidencePerFactor;

        confidence += (float)(Math.Min(Math.Abs(zScore) - _opts.EntryZScore, _opts.ZScoreMagnitudeCap) * _opts.ZScoreBonusMultiplier);

        if (hasFastHalfLife) confidence += _opts.FastHalfLifeBonus;

        if (adfStat < _opts.AdfCriticalValue - _opts.AdfBonusOffset) confidence += _opts.StrongStationarityBonus;

        if (!double.IsNaN(patterns.VwapDeviationSigmas))
        {
            bool vwapAgrees = (zScore > 0 && patterns.VwapDeviationSigmas > _opts.VwapAgreementThreshold)
                           || (zScore < 0 && patterns.VwapDeviationSigmas < -_opts.VwapAgreementThreshold);
            if (vwapAgrees)
            {
                confidence += _opts.VwapAgreementBonus;
                reasons.Add($"VWAP {patterns.VwapDeviationSigmas:F1}σ agrees");
            }
        }

        var direction = zScore > 0 ? "short" : "long";
        var reasoning = string.Join(" | ", reasons);

        return Task.FromResult(StrategyVote.Approve(StrategyTag, confidence,
            $"StatArb {direction} ({confirmedFactors}/4 factors): {reasoning}", priorWeight: SwarmWeight));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Statistical Methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Estimates the half-life of mean reversion using an Ornstein-Uhlenbeck model.
    /// Fits: ΔP(t) = λ × (P(t-1) - mean) + ε via OLS regression.
    /// Half-life = -ln(2) / λ. Returns -1 if λ ≥ 0 (no mean reversion).
    /// </summary>
    internal static double EstimateHalfLife(double[] prices)
    {
        if (prices.Length < 10) return -1;

        var mean = prices.Average();
        int n = prices.Length - 1;

        // y = ΔP(t), x = P(t-1) - mean
        double sumXY = 0, sumXX = 0;
        for (int i = 1; i < prices.Length; i++)
        {
            double x = prices[i - 1] - mean;
            double y = prices[i] - prices[i - 1];
            sumXY += x * y;
            sumXX += x * x;
        }

        if (sumXX < 1e-15) return -1;

        double lambda = sumXY / sumXX;

        // λ must be negative for mean reversion (pulls toward mean)
        if (lambda >= 0) return -1;

        return -Math.Log(2) / lambda;
    }

    /// <summary>
    /// Computes the Augmented Dickey-Fuller t-statistic for stationarity testing.
    /// Tests H0: unit root (non-stationary) vs H1: stationary.
    /// Uses the regression: ΔP(t) = α + β × P(t-1) + ε
    /// ADF stat = β / SE(β). More negative = stronger evidence of stationarity.
    /// </summary>
    internal static double ComputeAdfStatistic(double[] prices)
    {
        if (prices.Length < 10) return 0;

        int n = prices.Length - 1;

        // Build regression: y[i] = ΔP, x[i] = P(t-1)
        // OLS for y = α + β*x
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;

        for (int i = 1; i < prices.Length; i++)
        {
            double x = prices[i - 1];
            double y = prices[i] - prices[i - 1];
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double meanX = sumX / n;
        double meanY = sumY / n;

        double sxx = sumXX - n * meanX * meanX;
        if (Math.Abs(sxx) < 1e-15) return 0;

        double sxy = sumXY - n * meanX * meanY;
        double beta = sxy / sxx;
        double alpha = meanY - beta * meanX;

        // Compute residual standard error
        double sse = 0;
        for (int i = 1; i < prices.Length; i++)
        {
            double x = prices[i - 1];
            double y = prices[i] - prices[i - 1];
            double residual = y - alpha - beta * x;
            sse += residual * residual;
        }

        double mse = sse / (n - 2); // degrees of freedom = n - 2 (α and β)
        if (mse < 1e-15) return 0; // perfect fit with β≈0 = inconclusive (likely deterministic trend)

        double seBeta = Math.Sqrt(mse / sxx);
        if (seBeta < 1e-15) return 0;

        return beta / seBeta; // ADF t-statistic
    }
}
