using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Scans a candidate symbol universe for cointegrating pairs and ranks them by
/// a composite quality score (ADF strength, Hurst, correlation, HL sweet spot).
/// Outputs a promotion-ready list the PairsPortfolioCoordinator can spin up as
/// live engines.
///
/// Runs offline or on a cadence (hourly/daily) — not in the hot path. Complexity
/// O(N² · cointCost) per universe pass, so keep the universe ≤ ~50 symbols per run.
/// </summary>
public sealed class PairsDiscoveryService
{
    private readonly ILogger<PairsDiscoveryService> _logger;
    private readonly PairsTradingOptions _options;

    public PairsDiscoveryService(
        ILogger<PairsDiscoveryService> logger,
        IOptions<PairsTradingOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new PairsTradingOptions();
    }

    /// <summary>
    /// Test every unordered pair (i,j) with i&lt;j in the universe and return candidates
    /// ordered by descending quality score. Candidates failing the admission gate are
    /// dropped, not just ranked low.
    /// </summary>
    /// <param name="candles">Map of symbol → aligned close-price series. All series must
    /// be the same length; caller is responsible for timestamp alignment.</param>
    /// <param name="topN">Cap on returned candidates. 0 = return all passing.</param>
    public IReadOnlyList<PairCandidate> Discover(
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> candles,
        int topN = 10)
    {
        var symbols = candles.Keys.ToArray();
        if (symbols.Length < 2)
        {
            _logger.LogWarning("PairsDiscovery: universe too small ({N} symbols)", symbols.Length);
            return Array.Empty<PairCandidate>();
        }

        int? seriesLen = null;
        foreach (var s in symbols)
        {
            if (candles[s].Count == 0) continue;
            seriesLen ??= candles[s].Count;
            if (candles[s].Count != seriesLen)
            {
                _logger.LogError("PairsDiscovery: series length mismatch for {Symbol} ({Got} vs {Expected})",
                    s, candles[s].Count, seriesLen);
                return Array.Empty<PairCandidate>();
            }
        }

        var results = new List<PairCandidate>();
        int tested = 0, admitted = 0;

        for (int i = 0; i < symbols.Length; i++)
        {
            for (int j = i + 1; j < symbols.Length; j++)
            {
                var a = symbols[i];
                var b = symbols[j];
                tested++;

                // Two orderings — cointegration is asymmetric (Y = α + βX). Try both
                // and keep the stronger one.
                var forward = EngleGrangerDetector.TestCointegration(candles[a], candles[b]);
                var reverse = EngleGrangerDetector.TestCointegration(candles[b], candles[a]);

                var (best, symY, symX) = Math.Abs(forward.AdfTStatistic) >= Math.Abs(reverse.AdfTStatistic)
                    ? (forward, a, b)
                    : (reverse, b, a);

                if (!best.IsCointegrated) continue;

                // Apply the same options-level gates the engine uses.
                bool hurstReliable = candles[a].Count >= 200;
                if (Math.Abs(best.Correlation) < _options.MinAbsCorrelation) continue;
                if (hurstReliable && best.HurstExponent > _options.MaxHurstExponent) continue;
                if (best.HalfLife < _options.MinHalfLifeBars || best.HalfLife > _options.MaxHalfLifeBars) continue;
                var absBeta = Math.Abs(best.HedgeRatio);
                if (absBeta < _options.MinHedgeRatio || absBeta > _options.MaxHedgeRatio) continue;

                double score = ComputeQualityScore(best);
                results.Add(new PairCandidate(symY, symX, best, score));
                admitted++;
            }
        }

        results.Sort((l, r) => r.QualityScore.CompareTo(l.QualityScore));

        _logger.LogInformation(
            "PairsDiscovery done: tested={Tested} admitted={Admitted} universe={N}",
            tested, admitted, symbols.Length);

        if (topN > 0 && results.Count > topN)
            return results.Take(topN).ToList();
        return results;
    }

    /// <summary>
    /// Composite 0-100 score. Weights mirror the live engine's ComputePairConfidence:
    ///   Hurst 25, |corr| 15, HL-sweet 20, ADF |t| 25, |β|-sanity 15.
    /// Higher = better candidate for promotion.
    /// </summary>
    public static double ComputeQualityScore(EngleGrangerDetector.CointegrationResult r)
    {
        double hurstScore = r.HurstExponent <= 0.30 ? 25
            : r.HurstExponent <= 0.40 ? 20
            : r.HurstExponent <= 0.45 ? 14
            : r.HurstExponent <= 0.50 ? 7
            : 0;

        double absCorr = Math.Abs(r.Correlation);
        double corrScore = absCorr >= 0.80 ? 15 : absCorr >= 0.60 ? 11 : absCorr >= 0.40 ? 6 : 0;

        double hlScore = r.HalfLife switch
        {
            >= 5.0 and <= 30.0 => 20,
            >= 3.0 and <= 50.0 => 12,
            >= 2.0 and <= 80.0 => 6,
            _ => 0
        };

        double adfScore = r.AdfTStatistic <= -3.5 ? 25
            : r.AdfTStatistic <= -2.5 ? 18
            : r.AdfTStatistic <= -1.5 ? 10
            : r.AdfTStatistic <= -0.5 ? 3
            : 0;

        double absBeta = Math.Abs((double)r.HedgeRatio);
        double betaScore = absBeta is >= 0.5 and <= 2.0 ? 15
            : absBeta is >= 0.25 and <= 4.0 ? 8
            : 2;

        return Math.Clamp(hurstScore + corrScore + hlScore + adfScore + betaScore, 0, 100);
    }
}

/// <summary>
/// One discovered pair candidate ready for promotion. <paramref name="SymbolY"/> is the
/// dependent leg (regressed on X). <paramref name="QualityScore"/> is the composite 0-100
/// ranking used for coordinator admission.
/// </summary>
public sealed record PairCandidate(
    string SymbolY,
    string SymbolX,
    EngleGrangerDetector.CointegrationResult Diagnostics,
    double QualityScore);
