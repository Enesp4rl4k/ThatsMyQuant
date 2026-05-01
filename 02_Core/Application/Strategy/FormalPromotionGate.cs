using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Objective, criteria-based promotion gate for the backtest → paper → live pipeline.
/// Each stage has quantitative thresholds that MUST be met; no subjective overrides.
/// This is what separates "my bot works on backtests" from "my bot should run live".
///
/// Typical use: orchestrator runs backtest → gate evaluates → if pass, promote to
/// paper and keep evaluating paper-stage trade stream until live gate clears.
/// </summary>
public sealed class FormalPromotionGate
{
    private readonly ILogger<FormalPromotionGate> _logger;

    public FormalPromotionGate(ILogger<FormalPromotionGate> logger)
    {
        _logger = logger;
    }

    /// <summary>Backtest → Paper promotion.</summary>
    public PromotionDecision EvaluateBacktestToPaper(BacktestResult result, PromotionThresholds? thresholds = null)
    {
        var t = thresholds ?? PromotionThresholds.DefaultBacktestToPaper;
        var failures = new List<string>();
        var passes = new List<string>();

        Check(result.TotalTrades >= t.MinTrades, $"Trades {result.TotalTrades} ≥ {t.MinTrades}", failures, passes);
        Check(result.SharpeRatio >= t.MinSharpe, $"Sharpe {result.SharpeRatio:F2} ≥ {t.MinSharpe:F2}", failures, passes);
        Check(result.ProfitFactor >= t.MinProfitFactor, $"ProfitFactor {result.ProfitFactor:F2} ≥ {t.MinProfitFactor:F2}", failures, passes);
        Check(result.MaxDrawdownPercent <= t.MaxDrawdownPct, $"MaxDD {result.MaxDrawdownPercent:F1}% ≤ {t.MaxDrawdownPct:F1}%", failures, passes);
        Check(result.WinRate >= t.MinWinRate, $"WinRate {result.WinRate:F1}% ≥ {t.MinWinRate:F1}%", failures, passes);
        Check(result.AverageRR >= t.MinAvgRR, $"AvgRR {result.AverageRR:F2} ≥ {t.MinAvgRR:F2}", failures, passes);
        Check(result.CostLeakagePercent <= t.MaxCostLeakagePct, $"CostLeakage {result.CostLeakagePercent:F1}% ≤ {t.MaxCostLeakagePct:F1}%", failures, passes);

        return Finalise("BacktestToPaper", failures, passes);
    }

    /// <summary>Paper → Live promotion. Requires longer track record + cost parity.</summary>
    /// <param name="backtestResult">The backtest that cleared the paper gate.</param>
    /// <param name="paperResult">Paper trading stats over the evaluation window.</param>
    /// <param name="thresholds">Stage-specific thresholds; defaults stricter than backtest gate.</param>
    public PromotionDecision EvaluatePaperToLive(
        BacktestResult backtestResult,
        BacktestResult paperResult,
        PromotionThresholds? thresholds = null)
    {
        var t = thresholds ?? PromotionThresholds.DefaultPaperToLive;
        var failures = new List<string>();
        var passes = new List<string>();

        Check(paperResult.TotalTrades >= t.MinTrades, $"PaperTrades {paperResult.TotalTrades} ≥ {t.MinTrades}", failures, passes);
        Check(paperResult.SharpeRatio >= t.MinSharpe, $"PaperSharpe {paperResult.SharpeRatio:F2} ≥ {t.MinSharpe:F2}", failures, passes);
        Check(paperResult.ProfitFactor >= t.MinProfitFactor, $"PaperPF {paperResult.ProfitFactor:F2} ≥ {t.MinProfitFactor:F2}", failures, passes);
        Check(paperResult.MaxDrawdownPercent <= t.MaxDrawdownPct, $"PaperDD {paperResult.MaxDrawdownPercent:F1}% ≤ {t.MaxDrawdownPct:F1}%", failures, passes);

        // Parity check: paper Sharpe must be within tolerance of backtest Sharpe, else a
        // regime change / overfitting signal. Allow ±30% slack by default.
        double parityTolerance = t.PaperBacktestSharpeTolerance;
        double backSharpe = backtestResult.SharpeRatio;
        double paperSharpe = paperResult.SharpeRatio;
        bool parityOk = backSharpe <= 0 || Math.Abs(paperSharpe - backSharpe) / Math.Max(0.01, Math.Abs(backSharpe)) <= parityTolerance;
        Check(parityOk, $"Paper/Backtest Sharpe parity (|Δ|/|back| ≤ {parityTolerance:P0})", failures, passes);

        Check(paperResult.CostLeakagePercent <= t.MaxCostLeakagePct, $"PaperCostLeakage {paperResult.CostLeakagePercent:F1}% ≤ {t.MaxCostLeakagePct:F1}%", failures, passes);

        return Finalise("PaperToLive", failures, passes);
    }

    /// <summary>
    /// Statistical-rigor overlay: even when the basic gate clears, a strategy is only
    /// admitted to the next stage if its trade-level PnL passes (a) Bonferroni correction
    /// for the universe size we searched over, and (b) bootstrapped Sharpe lower-CI
    /// bound > 0, and (c) Deflated Sharpe Ratio probability ≥ 0.95.
    ///
    /// This is the missing gate identified in the 2026-04-30 audit — apparent edge on
    /// 30 trades has ~40% false-positive rate (Arnott et al. 2016) without
    /// multiplicity correction. Call this AFTER EvaluateBacktestToPaper / EvaluatePaperToLive
    /// returns Approved; if the rigor overlay fails, demote back to a warning state and
    /// require more trades before re-evaluating.
    /// </summary>
    /// <param name="stage">"BacktestToPaper" / "PaperToLive" — used in logging only.</param>
    /// <param name="tradePnls">Per-trade NetPnl in USD (or any consistent unit).</param>
    /// <param name="universeSize">Number of strategies/symbols you search across (Bonferroni N).</param>
    /// <param name="thresholds">Default 95% Sharpe lower-CI > 0 and DSR ≥ 0.95.</param>
    public PromotionDecision EvaluateStatisticalRigor(
        string stage,
        IReadOnlyList<decimal> tradePnls,
        int universeSize,
        StatisticalRigorThresholds? thresholds = null)
    {
        var t = thresholds ?? StatisticalRigorThresholds.Default;
        var failures = new List<string>();
        var passes = new List<string>();

        if (tradePnls.Count < 2)
        {
            failures.Add($"Sample too small for statistical rigor (N={tradePnls.Count} < 2)");
            return Finalise($"{stage}+Rigor", failures, passes);
        }

        var pnls = tradePnls.Select(x => (double)x).ToList();
        var tStat = StatisticalSignificance.TStatisticVsZero(pnls);
        var sharpe = StatisticalSignificance.SharpePerTrade(pnls);
        var (ciLow, ciHigh, _) = StatisticalSignificance.BootstrapSharpeCI(pnls);
        var dsr = StatisticalSignificance.DeflatedSharpeRatioProbability(pnls, universeSize, sharpe);
        var bonfRequired = StatisticalSignificance.BonferroniRequiredAbsT(universeSize);

        Check(Math.Abs(tStat) >= bonfRequired,
            $"|t-stat| {Math.Abs(tStat):F2} ≥ Bonferroni {bonfRequired:F2} (N={universeSize})",
            failures, passes);
        Check(ciLow > t.MinSharpeCiLowerBound,
            $"Sharpe 95% CI lower bound {ciLow:F2} > {t.MinSharpeCiLowerBound:F2}",
            failures, passes);
        Check(dsr >= t.MinDeflatedSharpeProbability,
            $"DeflatedSharpe P(SR>0) {dsr:F3} ≥ {t.MinDeflatedSharpeProbability:F2}",
            failures, passes);

        return Finalise($"{stage}+Rigor", failures, passes);
    }

    private static void Check(bool condition, string desc, List<string> failures, List<string> passes)
    {
        if (condition) passes.Add(desc); else failures.Add(desc);
    }

    private PromotionDecision Finalise(string stage, List<string> failures, List<string> passes)
    {
        bool approved = failures.Count == 0;
        var decision = new PromotionDecision(
            Stage: stage,
            Approved: approved,
            PassedCriteria: passes,
            FailedCriteria: failures);
        if (approved)
            _logger.LogInformation("Promotion {Stage} APPROVED | {Passed}/{Total} criteria", stage, passes.Count, passes.Count + failures.Count);
        else
            _logger.LogWarning("Promotion {Stage} REJECTED | failures: {Failures}", stage, string.Join(" ; ", failures));
        return decision;
    }
}

/// <summary>Stage thresholds for promotion decisions. Defaults are calibrated for
/// crypto intraday strategies; tighten for production.</summary>
public sealed record PromotionThresholds(
    int MinTrades,
    double MinSharpe,
    double MinProfitFactor,
    decimal MaxDrawdownPct,
    double MinWinRate,
    double MinAvgRR,
    double MaxCostLeakagePct,
    double PaperBacktestSharpeTolerance)
{
    public static PromotionThresholds DefaultBacktestToPaper => new(
        MinTrades: 50,
        MinSharpe: 1.0,
        MinProfitFactor: 1.3,
        MaxDrawdownPct: 15m,
        MinWinRate: 40.0,
        MinAvgRR: 1.2,
        MaxCostLeakagePct: 40.0,
        PaperBacktestSharpeTolerance: 1.0);

    public static PromotionThresholds DefaultPaperToLive => new(
        MinTrades: 30,
        MinSharpe: 0.9,
        MinProfitFactor: 1.25,
        MaxDrawdownPct: 10m,
        MinWinRate: 40.0,
        MinAvgRR: 1.15,
        MaxCostLeakagePct: 45.0,
        PaperBacktestSharpeTolerance: 0.30);
}

public sealed record PromotionDecision(
    string Stage,
    bool Approved,
    List<string> PassedCriteria,
    List<string> FailedCriteria);

/// <summary>
/// Defaults for the statistical-rigor overlay. CI-lower > 0 and DSR ≥ 0.95 are
/// industry-standard "Bailey & López de Prado" gates. Loosen with caution.
/// </summary>
public sealed record StatisticalRigorThresholds(
    double MinSharpeCiLowerBound,
    double MinDeflatedSharpeProbability)
{
    public static StatisticalRigorThresholds Default => new(
        MinSharpeCiLowerBound: 0.0,
        MinDeflatedSharpeProbability: 0.95);

    /// <summary>Looser overlay for early-stage discovery (DSR ≥ 0.80, CI ≥ -0.1).</summary>
    public static StatisticalRigorThresholds Discovery => new(
        MinSharpeCiLowerBound: -0.1,
        MinDeflatedSharpeProbability: 0.80);
}
