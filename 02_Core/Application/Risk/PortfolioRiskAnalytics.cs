using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// Portfolio-level risk analytics separate from the per-trade RiskAgent. Exposes:
///   • Fractional Kelly sizing from historical win-rate / avg-win / avg-loss.
///   • Historical VaR (Value-at-Risk) at a confidence level.
///   • Historical CVaR (Conditional Value-at-Risk / Expected Shortfall) — the
///     mean loss *conditional on being in the worst tail*. CVaR is coherent where
///     VaR isn't, and is what regulators (Basel) have moved to.
///
/// These are *analytics*, not gates. The RiskAgent consumes them for sizing and
/// emergency-liquidate decisions. Keeping them standalone makes them unit-testable
/// and reusable across backtest/paper/live.
/// </summary>
public sealed class PortfolioRiskAnalytics
{
    private readonly ILogger<PortfolioRiskAnalytics> _logger;

    public PortfolioRiskAnalytics(ILogger<PortfolioRiskAnalytics> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Fractional Kelly position size as a fraction of equity.
    ///   f* = p/L - q/W   (p=winRate, q=1-p, W=avgWin/lossUnit, L=avgLoss/lossUnit)
    /// We halve it (Kelly/2) because full Kelly is optimal in expectation but catastrophic
    /// when win-rate is estimated with error, which it always is.
    ///
    /// Caps at 25% — no single trade should ever go above 1/4 equity no matter the edge.
    /// </summary>
    public decimal ComputeFractionalKelly(IReadOnlyList<BacktestTradeResult> trades, decimal fraction = 0.5m, decimal cap = 0.25m)
    {
        if (trades.Count < 20) return 0m; // insufficient sample — no Kelly
        int wins = 0, losses = 0;
        decimal sumWin = 0m, sumLoss = 0m;
        foreach (var t in trades)
        {
            if (t.PnL > 0) { wins++; sumWin += t.PnL; }
            else if (t.PnL < 0) { losses++; sumLoss += Math.Abs(t.PnL); }
        }
        if (wins == 0 || losses == 0) return 0m;

        decimal p = (decimal)wins / trades.Count;
        decimal q = 1m - p;
        decimal avgWin = sumWin / wins;
        decimal avgLoss = sumLoss / losses;
        if (avgLoss <= 0) return 0m;

        // Kelly: f* = p/L - q/W   where L = avgLoss, W = avgWin   when bet size normalised to avgLoss.
        // Equivalent form using win/loss ratio b = avgWin/avgLoss:  f* = (p·b - q) / b
        decimal b = avgWin / avgLoss;
        decimal fullKelly = (p * b - q) / b;
        if (fullKelly <= 0) return 0m;

        decimal sized = fullKelly * fraction;
        return Math.Min(sized, cap);
    }

    /// <summary>
    /// Historical VaR at confidence level α (e.g. 0.95 = 95% VaR).
    /// Returns a *positive* loss magnitude: "we should not lose more than VaR in α of cases".
    /// </summary>
    public decimal ComputeHistoricalVaR(IReadOnlyList<decimal> periodicReturns, double confidence = 0.95)
    {
        if (periodicReturns.Count < 10) return 0m;
        var sorted = periodicReturns.OrderBy(x => x).ToArray();
        int idx = (int)Math.Floor((1.0 - confidence) * sorted.Length);
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return Math.Max(0m, -sorted[idx]); // negate worst-tail return to get loss magnitude
    }

    /// <summary>
    /// Historical CVaR / Expected Shortfall at confidence level α.
    /// Mean of the worst (1-α) fraction of returns. More conservative than VaR and coherent.
    /// </summary>
    public decimal ComputeHistoricalCVaR(IReadOnlyList<decimal> periodicReturns, double confidence = 0.95)
    {
        if (periodicReturns.Count < 10) return 0m;
        var sorted = periodicReturns.OrderBy(x => x).ToArray();
        int tailCount = Math.Max(1, (int)Math.Floor((1.0 - confidence) * sorted.Length));
        decimal tailSum = 0m;
        for (int i = 0; i < tailCount; i++) tailSum += sorted[i];
        decimal meanTail = tailSum / tailCount;
        return Math.Max(0m, -meanTail);
    }

    /// <summary>One-shot combined portfolio risk snapshot.</summary>
    public PortfolioRiskReport BuildReport(
        IReadOnlyList<BacktestTradeResult> trades,
        IReadOnlyList<decimal> periodicReturns,
        double confidence = 0.95,
        decimal kellyFraction = 0.5m)
    {
        var kelly = ComputeFractionalKelly(trades, kellyFraction);
        var var95 = ComputeHistoricalVaR(periodicReturns, confidence);
        var cvar95 = ComputeHistoricalCVaR(periodicReturns, confidence);

        _logger.LogInformation(
            "PortfolioRisk: Kelly={K:P2} VaR{C:P0}={V:P2} CVaR{C:P0}={CV:P2}",
            kelly, confidence, var95, confidence, cvar95);

        return new PortfolioRiskReport(kelly, var95, cvar95, confidence, trades.Count, periodicReturns.Count);
    }
}

public sealed record PortfolioRiskReport(
    decimal FractionalKelly,
    decimal HistoricalVaR,
    decimal HistoricalCVaR,
    double ConfidenceLevel,
    int TradeSampleSize,
    int ReturnSampleSize);
