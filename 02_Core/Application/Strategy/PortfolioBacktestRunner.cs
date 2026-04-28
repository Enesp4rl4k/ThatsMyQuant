using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy.ProMode;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Combined-portfolio backtest. Runs N heterogeneous strategies (funding alpha,
/// pairs, etc.) independently on aligned historical data, then aggregates their
/// trade PnL into a single daily-bucketed equity curve. This measures the REAL
/// portfolio-level Sharpe, drawdown, and inter-strategy correlation — replacing
/// the theoretical √N projection with empirical evidence.
///
/// Mechanics:
///  1. Each strategy runs with its own capital allocation (Kelly-weighted).
///  2. Trade-level PnL is bucketed into daily windows by EXIT date.
///  3. Daily PnL summed across strategies → portfolio daily returns.
///  4. Reports: portfolio Sharpe, correlation matrix, max DD, combined PnL.
///
/// Correlation interpretation:
///   ≤0.3  = true diversification (√N Sharpe boost close to theoretical)
///   0.3–0.6 = moderate diversification
///   ≥0.6  = crowded thesis — diversification benefit limited
/// </summary>
public sealed class PortfolioBacktestRunner
{
    private readonly ILogger<PortfolioBacktestRunner> _logger;
    private readonly FundingAlphaBacktestRunner _fundingRunner;
    private readonly PairsBacktestRunner _pairsRunner;
    private readonly IHistoricalMarketDataService _history;
    private readonly ConcentrationGuard? _concentrationGuard;
    private readonly HashSet<string> _regimeGateOptInPairs;

    public PortfolioBacktestRunner(
        ILogger<PortfolioBacktestRunner> logger,
        FundingAlphaBacktestRunner fundingRunner,
        PairsBacktestRunner pairsRunner,
        IHistoricalMarketDataService history,
        ConcentrationGuard? concentrationGuard = null,
        IOptions<PairsTradingOptions>? pairsOptions = null)
    {
        _logger = logger;
        _fundingRunner = fundingRunner;
        _pairsRunner = pairsRunner;
        _history = history;
        _concentrationGuard = concentrationGuard;

        // [F1 per-pair opt-in] Parse the CSV into a fast-lookup set. Empty = no opt-ins
        // → all pairs use the global RequireRangingRegime default (false).
        _regimeGateOptInPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var csv = pairsOptions?.Value?.RegimeGateOptInPairsCsv;
        if (!string.IsNullOrWhiteSpace(csv))
        {
            foreach (var label in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _regimeGateOptInPairs.Add(label);
        }
    }

    /// <summary>
    /// Reduce "TRX-USDT" / "XLM-USDT" → "TRX-XLM" pair label used for opt-in lookup.
    /// </summary>
    private static string BuildPairLabel(string symbolA, string symbolB)
    {
        static string Strip(string s)
        {
            var i = s.IndexOf('-');
            return i > 0 ? s[..i] : s;
        }
        return $"{Strip(symbolA)}-{Strip(symbolB)}";
    }

    public Task<PortfolioBacktestResult> RunAsync(
        IReadOnlyList<PortfolioPosition> positions,
        DateTime startUtc,
        DateTime endUtc,
        decimal totalCapital,
        CancellationToken ct = default)
        => RunAsync(positions, startUtc, endUtc, totalCapital, PortfolioWeightingMode.Fixed, ct);

    public async Task<PortfolioBacktestResult> RunAsync(
        IReadOnlyList<PortfolioPosition> positions,
        DateTime startUtc,
        DateTime endUtc,
        decimal totalCapital,
        PortfolioWeightingMode weightingMode,
        CancellationToken ct = default)
    {
        if (positions.Count == 0) throw new ArgumentException("No positions configured");

        var weightSum = positions.Sum(p => p.CapitalWeight);
        if (weightSum <= 0m) throw new ArgumentException("Total weight must be positive");

        // 1) Resolve effective weights (Fixed = config-as-is; InverseVolatility = 1/σ scaled;
        //    HierarchicalRiskParity = Lopez de Prado HRP via Ledoit-Wolf covariance).
        var effectiveWeights = weightingMode switch
        {
            PortfolioWeightingMode.InverseVolatility =>
                await ComputeInverseVolWeightsAsync(positions, startUtc, endUtc, ct).ConfigureAwait(false),
            PortfolioWeightingMode.HierarchicalRiskParity =>
                await ComputeHrpWeightsAsync(positions, startUtc, endUtc, ct).ConfigureAwait(false),
            PortfolioWeightingMode.BlackLitterman =>
                await ComputeBlackLittermanWeightsAsync(positions, startUtc, endUtc, ct).ConfigureAwait(false),
            _ => NormalizeFixed(positions, weightSum)
        };

        // 2) Apply ConcentrationGuard caps (25%/60%/40%) so single legs cannot dominate.
        //    Excess weight is held as cash (un-invested), preserving total-capital semantics.
        var guarded = ApplyConcentrationGuard(positions, effectiveWeights);

        var dailyPnlByStrategy = new Dictionary<string, SortedDictionary<DateTime, decimal>>();
        var strategyMetrics = new List<StrategyResult>();

        for (int idx = 0; idx < positions.Count; idx++)
        {
            ct.ThrowIfCancellationRequested();
            var pos = positions[idx];
            var w = guarded[idx];
            var capital = totalCapital * w;
            _logger.LogInformation(
                "Running portfolio leg: {Name} ({Type}) capital={Cap:N0} weight={W:P2} mode={Mode}",
                pos.Name, pos.Type, capital, w, weightingMode);

            var bucketsForLeg = new SortedDictionary<DateTime, decimal>();
            var legResult = await RunLegAsync(pos, capital, startUtc, endUtc, bucketsForLeg, ct).ConfigureAwait(false);
            dailyPnlByStrategy[pos.Name] = bucketsForLeg;
            strategyMetrics.Add(legResult);
        }

        // Build union of all trading dates.
        var allDates = dailyPnlByStrategy.Values
            .SelectMany(d => d.Keys)
            .Distinct()
            .OrderBy(d => d)
            .ToArray();

        // Aggregate daily PnL across strategies.
        var portfolioDailyPnl = new decimal[allDates.Length];
        var perStrategyDailyPnl = new Dictionary<string, decimal[]>();
        foreach (var kv in dailyPnlByStrategy)
        {
            var arr = new decimal[allDates.Length];
            for (int i = 0; i < allDates.Length; i++)
            {
                arr[i] = kv.Value.TryGetValue(allDates[i], out var v) ? v : 0m;
                portfolioDailyPnl[i] += arr[i];
            }
            perStrategyDailyPnl[kv.Key] = arr;
        }

        // Portfolio equity curve + drawdown.
        decimal equity = totalCapital;
        decimal peak = totalCapital;
        decimal maxDd = 0m;
        var equityCurve = new decimal[allDates.Length];
        for (int i = 0; i < allDates.Length; i++)
        {
            equity += portfolioDailyPnl[i];
            equityCurve[i] = equity;
            if (equity > peak) peak = equity;
            var dd = peak > 0 ? (peak - equity) / peak : 0m;
            if (dd > maxDd) maxDd = dd;
        }

        // Daily returns for Sharpe.
        var dailyReturns = new double[allDates.Length];
        for (int i = 0; i < allDates.Length; i++)
            dailyReturns[i] = (double)(portfolioDailyPnl[i] / totalCapital);

        double portfolioSharpe = ComputeAnnualizedSharpe(dailyReturns);

        // Inter-strategy correlation matrix (Pearson on daily returns).
        var names = dailyPnlByStrategy.Keys.ToArray();
        var corrMatrix = new double[names.Length, names.Length];
        for (int i = 0; i < names.Length; i++)
        for (int j = 0; j < names.Length; j++)
        {
            if (i == j) { corrMatrix[i, j] = 1.0; continue; }
            var xi = perStrategyDailyPnl[names[i]].Select(v => (double)v).ToArray();
            var xj = perStrategyDailyPnl[names[j]].Select(v => (double)v).ToArray();
            corrMatrix[i, j] = PearsonCorrelation(xi, xj);
        }

        decimal totalPnl = equity - totalCapital;
        var report = new PortfolioBacktestResult(
            Positions: positions,
            StrategyResults: strategyMetrics,
            TotalCapital: totalCapital,
            FinalEquity: equity,
            TotalPnl: totalPnl,
            TotalPnlPct: totalCapital > 0 ? totalPnl / totalCapital : 0m,
            PortfolioSharpe: portfolioSharpe,
            MaxDrawdown: maxDd,
            TradingDays: allDates.Length,
            EquityCurve: equityCurve,
            Dates: allDates,
            StrategyNames: names,
            CorrelationMatrix: corrMatrix);

        _logger.LogInformation(
            "Portfolio backtest complete: equity {Start:N0} → {End:N0} | PnL {Pnl:N2} ({PnlPct:P2}) | Sharpe {S:F2} | MaxDD {DD:P2} | Days {D}",
            totalCapital, equity, totalPnl, report.TotalPnlPct, portfolioSharpe, maxDd, allDates.Length);

        return report;
    }

    private async Task<StrategyResult> RunLegAsync(
        PortfolioPosition pos,
        decimal capital,
        DateTime start,
        DateTime end,
        SortedDictionary<DateTime, decimal> daily,
        CancellationToken ct)
    {
        switch (pos.Type)
        {
            case PortfolioPositionType.FundingAlpha:
                return await RunFundingLegAsync(pos, capital, start, end, daily, ct).ConfigureAwait(false);
            case PortfolioPositionType.PairsArbitrage:
                return await RunPairsLegAsync(pos, capital, start, end, daily, ct).ConfigureAwait(false);
            default:
                throw new NotSupportedException($"Unsupported position type: {pos.Type}");
        }
    }

    private async Task<StrategyResult> RunFundingLegAsync(
        PortfolioPosition pos,
        decimal capital,
        DateTime start,
        DateTime end,
        SortedDictionary<DateTime, decimal> daily,
        CancellationToken ct)
    {
        var candles = await _history.GetHistoricalCandlesAsync(
            pos.SymbolA, pos.Timeframe, start, end, ct).ConfigureAwait(false);
        // Apply per-position leverage override to the funding config.
        var cfg = pos.Leverage != 1.0m && pos.FundingConfig is not null
            ? pos.FundingConfig with { Leverage = pos.Leverage }
            : pos.FundingConfig!;
        var result = await _fundingRunner.RunAsync(pos.SymbolA, candles, cfg, capital, ct)
            .ConfigureAwait(false);

        foreach (var t in result.Trades)
        {
            var day = t.ExitTimeUtc.Date;
            daily[day] = daily.TryGetValue(day, out var v) ? v + t.NetPnl : t.NetPnl;
        }

        return new StrategyResult(
            Name: pos.Name,
            Type: pos.Type.ToString(),
            Capital: capital,
            Trades: result.TotalTrades,
            WinRate: result.WinRate,
            NetPnl: result.TotalPnl,
            SharpeRatio: result.SharpeRatio,
            MaxDrawdownPct: result.MaxDrawdownPct,
            ProfitFactor: result.ProfitFactor);
    }

    private async Task<StrategyResult> RunPairsLegAsync(
        PortfolioPosition pos,
        decimal capital,
        DateTime start,
        DateTime end,
        SortedDictionary<DateTime, decimal> daily,
        CancellationToken ct)
    {
        var candlesA = await _history.GetHistoricalCandlesAsync(pos.SymbolA, pos.Timeframe, start, end, ct).ConfigureAwait(false);
        var candlesB = await _history.GetHistoricalCandlesAsync(pos.SymbolB!, pos.Timeframe, start, end, ct).ConfigureAwait(false);

        // [F1 per-pair opt-in] If this pair is in RegimeGateOptInPairsCsv, force the
        // regime gate ON for this leg even when the global default is OFF.
        var pairLabel = BuildPairLabel(pos.SymbolA, pos.SymbolB!);
        bool? regimeOverride = _regimeGateOptInPairs.Contains(pairLabel) ? true : null;
        if (regimeOverride is true)
            _logger.LogInformation("Pairs {Label}: regime gate (F1) force-enabled via opt-in CSV.", pairLabel);

        var result = _pairsRunner.Run(
            candlesA, candlesB, capital,
            leverageOverride: pos.Leverage,
            requireRangingRegimeOverride: regimeOverride);

        if (result.Trades is not null)
        {
            foreach (var t in result.Trades)
            {
                var day = t.ExitTime.Date;
                daily[day] = daily.TryGetValue(day, out var v) ? v + t.PnL : t.PnL;
            }
        }

        return new StrategyResult(
            Name: pos.Name,
            Type: pos.Type.ToString(),
            Capital: capital,
            Trades: result.TotalTrades,
            WinRate: result.WinRate / 100.0,
            NetPnl: result.TotalPnL,
            SharpeRatio: result.SharpeRatio,
            MaxDrawdownPct: result.MaxDrawdownPercent / 100m,
            ProfitFactor: (decimal)result.ProfitFactor);
    }

    private static decimal[] NormalizeFixed(IReadOnlyList<PortfolioPosition> positions, decimal weightSum)
    {
        var w = new decimal[positions.Count];
        for (int i = 0; i < positions.Count; i++)
            w[i] = positions[i].CapitalWeight / weightSum;
        return w;
    }

    /// <summary>
    /// Inverse-volatility weighting: w_i ∝ 1/σ_i where σ_i is the std-dev of daily
    /// log-returns of the position's primary symbol over [startUtc, endUtc]. Caps
    /// single-leg dominance from a high-PF outlier (e.g. TRX/XLM), which fixed-weight
    /// allocation cannot. If a symbol has insufficient history, it falls back to its
    /// configured CapitalWeight.
    /// </summary>
    private async Task<decimal[]> ComputeInverseVolWeightsAsync(
        IReadOnlyList<PortfolioPosition> positions,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken ct)
    {
        var invVol = new decimal[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pos = positions[i];
            try
            {
                var candles = await _history.GetHistoricalCandlesAsync(pos.SymbolA, pos.Timeframe, startUtc, endUtc, ct)
                    .ConfigureAwait(false);
                var sigma = ComputeReturnStdDev(candles);
                invVol[i] = sigma > 0m ? 1m / sigma : pos.CapitalWeight;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "InverseVol fallback to fixed weight for {Name}", pos.Name);
                invVol[i] = pos.CapitalWeight;
            }
        }
        var sum = invVol.Sum();
        if (sum <= 0m) return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
        for (int i = 0; i < invVol.Length; i++) invVol[i] /= sum;
        return invVol;
    }

    private async Task<decimal[]> ComputeHrpWeightsAsync(
        IReadOnlyList<PortfolioPosition> positions,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken ct)
    {
        // Pull leg-A return series for each position over the backtest window.
        // Truncate / align to the shortest length so we have a clean rectangular [T×N] panel.
        int n = positions.Count;
        var perAssetLogReturns = new List<double[]>(n);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pos = positions[i];
            try
            {
                var candles = await _history.GetHistoricalCandlesAsync(pos.SymbolA, pos.Timeframe, startUtc, endUtc, ct)
                    .ConfigureAwait(false);
                if (candles == null || candles.Count < 30)
                {
                    _logger.LogWarning("HRP fallback to fixed weight (insufficient candles) for {Name}", pos.Name);
                    return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
                }
                var rets = new List<double>(candles.Count - 1);
                for (int k = 1; k < candles.Count; k++)
                {
                    var prev = (double)candles[k - 1].Close;
                    var cur = (double)candles[k].Close;
                    if (prev <= 0 || cur <= 0) continue;
                    rets.Add(Math.Log(cur / prev));
                }
                perAssetLogReturns.Add(rets.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HRP fallback to fixed weight (data fetch error) for {Name}", pos.Name);
                return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
            }
        }

        // Align to shortest length.
        int minLen = perAssetLogReturns.Min(r => r.Length);
        if (minLen < 30 || n < 2)
        {
            _logger.LogWarning("HRP fallback (minLen={MinLen}, N={N})", minLen, n);
            return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
        }

        // Build [T × N] return matrix using the LAST minLen observations of each series.
        var returnMatrix = new double[minLen, n];
        for (int j = 0; j < n; j++)
        {
            int offset = perAssetLogReturns[j].Length - minLen;
            for (int t = 0; t < minLen; t++)
            {
                returnMatrix[t, j] = perAssetLogReturns[j][offset + t];
            }
        }

        // Ledoit-Wolf shrinkage covariance → HRP weights.
        try
        {
            var lw = LedoitWolfShrinkage.Estimate(returnMatrix);
            var hrp = HierarchicalRiskParity.Allocate(lw.Covariance);
            var weights = new decimal[n];
            for (int j = 0; j < n; j++) weights[j] = (decimal)hrp.Weights[j];

            _logger.LogInformation(
                "HRP weights computed (Ledoit-Wolf δ={Delta:F3}, avgCorr={Corr:F3}): {Summary}",
                lw.Delta, lw.AverageCorrelation,
                string.Join(", ", positions.Select((p, i) => $"{p.Name}={weights[i]:P1}")));
            return weights;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HRP computation failed; falling back to fixed weights.");
            return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
        }
    }

    private async Task<decimal[]> ComputeBlackLittermanWeightsAsync(
        IReadOnlyList<PortfolioPosition> positions,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken ct)
    {
        // Black-Litterman with config-CapitalWeights as the equilibrium prior, no active views.
        // Uses Ledoit-Wolf covariance from the same return panel HRP uses; produces a prior-anchored
        // posterior re-weighted by Markowitz with the given risk-aversion.
        int n = positions.Count;
        var perAssetLogReturns = new List<double[]>(n);
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pos = positions[i];
            try
            {
                var candles = await _history.GetHistoricalCandlesAsync(pos.SymbolA, pos.Timeframe, startUtc, endUtc, ct)
                    .ConfigureAwait(false);
                if (candles == null || candles.Count < 30)
                {
                    _logger.LogWarning("Black-Litterman fallback to fixed for {Name} (insufficient candles)", pos.Name);
                    return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
                }
                var rets = new List<double>(candles.Count - 1);
                for (int k = 1; k < candles.Count; k++)
                {
                    var prev = (double)candles[k - 1].Close;
                    var cur = (double)candles[k].Close;
                    if (prev <= 0 || cur <= 0) continue;
                    rets.Add(Math.Log(cur / prev));
                }
                perAssetLogReturns.Add(rets.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Black-Litterman fallback to fixed (data error) for {Name}", pos.Name);
                return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
            }
        }

        int minLen = perAssetLogReturns.Min(r => r.Length);
        if (minLen < 30 || n < 2)
        {
            return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
        }

        var returnMatrix = new double[minLen, n];
        for (int j = 0; j < n; j++)
        {
            int offset = perAssetLogReturns[j].Length - minLen;
            for (int t = 0; t < minLen; t++) returnMatrix[t, j] = perAssetLogReturns[j][offset + t];
        }

        try
        {
            var lw = LedoitWolfShrinkage.Estimate(returnMatrix);
            // Equilibrium weights = config CapitalWeights (normalized to sum 1).
            double weightSum = (double)positions.Sum(p => p.CapitalWeight);
            var equilibriumWeights = positions.Select(p => weightSum > 0 ? (double)p.CapitalWeight / weightSum : 1.0 / n).ToArray();
            // Reverse-optimization to get implied returns π = λ Σ w.
            var pi = BlackLittermanPortfolio.ImpliedEquilibriumReturns(lw.Covariance, equilibriumWeights, riskAversion: 2.5);
            // No active views — posterior == prior.
            var bl = BlackLittermanPortfolio.Allocate(
                priorReturns: pi,
                covariance: lw.Covariance,
                viewMatrix: new double[0, n],
                viewReturns: Array.Empty<double>(),
                viewUncertainty: Array.Empty<double>(),
                tau: 0.05,
                riskAversion: 2.5,
                longOnly: true);
            var result = new decimal[n];
            for (int j = 0; j < n; j++) result[j] = (decimal)Math.Max(0.0, bl.Weights[j]);
            // Normalize to sum 1.
            decimal sum = result.Sum();
            if (sum <= 0) return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
            for (int j = 0; j < n; j++) result[j] /= sum;
            _logger.LogInformation(
                "Black-Litterman weights (LW δ={Delta:F3}): {Summary}",
                lw.Delta,
                string.Join(", ", positions.Select((p, i) => $"{p.Name}={result[i]:P1}")));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Black-Litterman computation failed; fallback to fixed.");
            return NormalizeFixed(positions, positions.Sum(p => p.CapitalWeight));
        }
    }

    private static decimal ComputeReturnStdDev(IReadOnlyList<Candle> candles)
    {
        if (candles is null || candles.Count < 3) return 0m;
        var rets = new List<double>(candles.Count - 1);
        for (int i = 1; i < candles.Count; i++)
        {
            var prev = (double)candles[i - 1].Close;
            var cur = (double)candles[i].Close;
            if (prev <= 0 || cur <= 0) continue;
            rets.Add(Math.Log(cur / prev));
        }
        if (rets.Count < 2) return 0m;
        var mean = rets.Average();
        var variance = rets.Sum(r => (r - mean) * (r - mean)) / (rets.Count - 1);
        return (decimal)Math.Sqrt(variance);
    }

    /// <summary>
    /// Applies ConcentrationGuard caps. Pairs and Funding are separate families;
    /// for Pairs the SymbolKey is the leg-A coin so SOL appearing in SOL/AVAX and
    /// SOL/LINK is jointly capped at 40%. Excess weight stays as cash (no rescale).
    /// </summary>
    private decimal[] ApplyConcentrationGuard(
        IReadOnlyList<PortfolioPosition> positions,
        decimal[] normalizedWeights)
    {
        if (_concentrationGuard is null) return normalizedWeights;

        var proposal = new List<PositionWeight>(positions.Count);
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var family = p.Type == PortfolioPositionType.PairsArbitrage ? "Pairs" : "Funding";
            proposal.Add(new PositionWeight(p.Name, p.SymbolA, family, normalizedWeights[i]));
        }
        var adjustment = _concentrationGuard.Enforce(proposal);
        var byName = adjustment.Adjusted.ToDictionary(a => a.Name, a => a.Weight);
        var result = new decimal[positions.Count];
        for (int i = 0; i < positions.Count; i++)
            result[i] = byName.TryGetValue(positions[i].Name, out var w) ? w : normalizedWeights[i];
        if (adjustment.Violations.Count > 0)
            _logger.LogInformation(
                "ConcentrationGuard adjusted {N} weight(s); excess-to-cash {Excess:P2}: {Violations}",
                adjustment.Violations.Count, adjustment.ExcessToCash, string.Join("; ", adjustment.Violations));
        return result;
    }

    private static double ComputeAnnualizedSharpe(double[] dailyReturns)
    {
        if (dailyReturns.Length < 2) return 0.0;
        double mean = dailyReturns.Average();
        double variance = dailyReturns.Sum(r => (r - mean) * (r - mean)) / (dailyReturns.Length - 1);
        double std = Math.Sqrt(variance);
        if (std < 1e-12) return 0.0;
        return (mean / std) * Math.Sqrt(365.0);
    }

    private static double PearsonCorrelation(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2) return 0.0;
        double mx = x.Average(), my = y.Average();
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            num += (x[i] - mx) * (y[i] - my);
            dx += (x[i] - mx) * (x[i] - mx);
            dy += (y[i] - my) * (y[i] - my);
        }
        if (dx < 1e-12 || dy < 1e-12) return 0.0;
        return num / Math.Sqrt(dx * dy);
    }
}

public enum PortfolioPositionType { FundingAlpha, PairsArbitrage }

/// <summary>
/// How to translate per-position CapitalWeight into the actual capital share.
/// Fixed = use CapitalWeight as-is (legacy behaviour).
/// InverseVolatility = w ∝ 1/σ over the backtest window — caps single-leg dominance.
/// HierarchicalRiskParity = Lopez de Prado HRP using Ledoit-Wolf shrinkage covariance.
/// BlackLitterman = Bayesian posterior using config CapitalWeights as the equilibrium prior
/// and Ledoit-Wolf for covariance; produces stable weights anchored to user intent but
/// adjusted for measured covariance structure.
/// </summary>
public enum PortfolioWeightingMode { Fixed, InverseVolatility, HierarchicalRiskParity, BlackLitterman }

public sealed record PortfolioPosition(
    string Name,
    PortfolioPositionType Type,
    string SymbolA,
    string? SymbolB,
    string Timeframe,
    decimal CapitalWeight,
    FundingAlphaConfig? FundingConfig = null,
    decimal Leverage = 1.0m);

public sealed record StrategyResult(
    string Name,
    string Type,
    decimal Capital,
    int Trades,
    double WinRate,
    decimal NetPnl,
    double SharpeRatio,
    decimal MaxDrawdownPct,
    decimal ProfitFactor);

public sealed record PortfolioBacktestResult(
    IReadOnlyList<PortfolioPosition> Positions,
    IReadOnlyList<StrategyResult> StrategyResults,
    decimal TotalCapital,
    decimal FinalEquity,
    decimal TotalPnl,
    decimal TotalPnlPct,
    double PortfolioSharpe,
    decimal MaxDrawdown,
    int TradingDays,
    decimal[] EquityCurve,
    DateTime[] Dates,
    string[] StrategyNames,
    double[,] CorrelationMatrix);
