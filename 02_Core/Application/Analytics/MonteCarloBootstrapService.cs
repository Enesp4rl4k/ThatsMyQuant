using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Monte Carlo bootstrap on a backtest trade stream. Resamples trades with
/// replacement N times to build empirical confidence intervals for Sharpe,
/// max-drawdown and terminal equity.
///
/// Why this matters: a single backtest gives one realised path. A strategy with
/// Sharpe 1.5 on a 100-trade sample may have a 5th-percentile Sharpe of 0.3 —
/// meaning it's one unlucky streak from being indistinguishable from noise.
/// Professional desks reject strategies whose CI straddles zero.
/// </summary>
public sealed class MonteCarloBootstrapService
{
    private readonly ILogger<MonteCarloBootstrapService> _logger;

    public MonteCarloBootstrapService(ILogger<MonteCarloBootstrapService> logger)
    {
        _logger = logger;
    }

    public BootstrapReport Analyse(
        BacktestResult backtest,
        int iterations = 1000,
        int? randomSeed = null)
    {
        var trades = backtest.Trades;
        if (trades.Count < 20)
        {
            _logger.LogWarning("MC Bootstrap: too few trades ({N}) for reliable CI", trades.Count);
            return BootstrapReport.Empty;
        }

        var rng = randomSeed is null ? new Random() : new Random(randomSeed.Value);
        int n = trades.Count;
        decimal startingEquity = backtest.StartingCapital;

        var sharpeSamples = new double[iterations];
        var terminalEquitySamples = new decimal[iterations];
        var maxDdPctSamples = new decimal[iterations];

        for (int k = 0; k < iterations; k++)
        {
            decimal equity = startingEquity;
            decimal peak = startingEquity;
            decimal worstDd = 0m;
            var returns = new double[n];

            for (int i = 0; i < n; i++)
            {
                int idx = rng.Next(n);
                var t = trades[idx];
                decimal pnl = t.PnL;
                decimal pre = equity;
                equity += pnl;
                returns[i] = pre > 0 ? (double)(pnl / pre) : 0.0;
                if (equity > peak) peak = equity;
                decimal dd = peak > 0 ? (peak - equity) / peak * 100m : 0m;
                if (dd > worstDd) worstDd = dd;
            }

            sharpeSamples[k] = ComputeSharpe(returns);
            terminalEquitySamples[k] = equity;
            maxDdPctSamples[k] = worstDd;
        }

        Array.Sort(sharpeSamples);
        Array.Sort(terminalEquitySamples);
        Array.Sort(maxDdPctSamples);

        var report = new BootstrapReport(
            Iterations: iterations,
            TradeCount: n,
            SharpeMedian: Percentile(sharpeSamples, 0.50),
            Sharpe5Pct: Percentile(sharpeSamples, 0.05),
            Sharpe95Pct: Percentile(sharpeSamples, 0.95),
            TerminalEquityMedian: PercentileDec(terminalEquitySamples, 0.50),
            TerminalEquity5Pct: PercentileDec(terminalEquitySamples, 0.05),
            TerminalEquity95Pct: PercentileDec(terminalEquitySamples, 0.95),
            MaxDrawdownPctMedian: PercentileDec(maxDdPctSamples, 0.50),
            MaxDrawdownPct95Pct: PercentileDec(maxDdPctSamples, 0.95),
            ProbabilityOfLoss: (double)sharpeSamples.Count(s => s <= 0) / iterations);

        _logger.LogInformation(
            "MC Bootstrap done: Sharpe 5%-50%-95% = {S5:F2} / {S50:F2} / {S95:F2} | P(loss)={PL:P1}",
            report.Sharpe5Pct, report.SharpeMedian, report.Sharpe95Pct, report.ProbabilityOfLoss);

        return report;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int idx = (int)Math.Clamp(Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static decimal PercentileDec(decimal[] sorted, double p)
    {
        if (sorted.Length == 0) return 0m;
        int idx = (int)Math.Clamp(Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static double ComputeSharpe(double[] returns)
    {
        if (returns.Length < 2) return 0;
        double mean = 0;
        for (int i = 0; i < returns.Length; i++) mean += returns[i];
        mean /= returns.Length;
        double var = 0;
        for (int i = 0; i < returns.Length; i++) { double d = returns[i] - mean; var += d * d; }
        var /= (returns.Length - 1);
        double std = Math.Sqrt(var);
        return std <= 0 ? 0 : (mean / std) * Math.Sqrt(252.0);
    }
}

/// <summary>Empirical confidence intervals from trade resampling.</summary>
/// <param name="Sharpe5Pct">5th percentile Sharpe — pessimistic scenario.</param>
/// <param name="Sharpe95Pct">95th percentile Sharpe — lucky scenario.</param>
/// <param name="ProbabilityOfLoss">Fraction of resampled paths with Sharpe ≤ 0.
/// If &gt; ~15% the strategy's edge is statistically shaky.</param>
public sealed record BootstrapReport(
    int Iterations,
    int TradeCount,
    double SharpeMedian,
    double Sharpe5Pct,
    double Sharpe95Pct,
    decimal TerminalEquityMedian,
    decimal TerminalEquity5Pct,
    decimal TerminalEquity95Pct,
    decimal MaxDrawdownPctMedian,
    decimal MaxDrawdownPct95Pct,
    double ProbabilityOfLoss)
{
    public static BootstrapReport Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
