using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Post-trade Transaction Cost Analysis (TCA). Decomposes realised PnL into
/// gross-alpha, explicit cost (commission+funding), and implicit cost (slippage),
/// then attributes each component to strategy / regime / exit-policy buckets.
///
/// Professional desks use TCA to (a) identify strategies whose edge is eaten by
/// costs and (b) detect broker/venue degradation (rising slippage).
/// </summary>
public sealed class TransactionCostAnalysisService
{
    private readonly ILogger<TransactionCostAnalysisService> _logger;

    public TransactionCostAnalysisService(ILogger<TransactionCostAnalysisService> logger)
    {
        _logger = logger;
    }

    public TcaReport Analyse(IReadOnlyList<BacktestTradeResult> trades, decimal? modeledSlippagePerTrade = null)
    {
        if (trades.Count == 0)
            return new TcaReport(0, 0, 0, 0, 0, 0, 0, new(), new(), new());

        decimal grossPnL = 0m, netPnL = 0m, fees = 0m, funding = 0m;
        foreach (var t in trades)
        {
            grossPnL += t.PnL + t.TotalFeesPaid - t.NetFunding;
            netPnL += t.PnL;
            fees += t.TotalFeesPaid;
            funding += t.NetFunding;
        }

        // Implicit cost = modelled slippage × trade count (absent tick-by-tick fills).
        decimal implicitCost = (modeledSlippagePerTrade ?? 0m) * trades.Count;

        // Cost leakage: what % of the gross alpha was consumed by explicit+implicit costs.
        decimal totalCost = fees + implicitCost + Math.Max(0m, -funding);
        double leakagePct = grossPnL > 0 ? (double)(totalCost / grossPnL) * 100.0 : 0.0;

        var byStrategy = AggregateBy(trades, t => t.StrategyTag);
        var byRegime = AggregateBy(trades, t => t.RegimeTag);
        var byExit = AggregateBy(trades, t => t.ExitPolicyTag);

        _logger.LogInformation(
            "TCA done: trades={N} grossPnL={G:F2} netPnL={N2:F2} fees={F:F2} leakage={L:F1}%",
            trades.Count, grossPnL, netPnL, fees, leakagePct);

        return new TcaReport(
            TradeCount: trades.Count,
            GrossPnL: grossPnL,
            NetPnL: netPnL,
            TotalFees: fees,
            NetFunding: funding,
            ImplicitSlippageCost: implicitCost,
            CostLeakagePercent: leakagePct,
            ByStrategy: byStrategy,
            ByRegime: byRegime,
            ByExitPolicy: byExit);
    }

    private static Dictionary<string, TcaBucket> AggregateBy(
        IReadOnlyList<BacktestTradeResult> trades,
        Func<BacktestTradeResult, string> keySelector)
    {
        var result = new Dictionary<string, TcaBucket>();
        foreach (var group in trades.GroupBy(keySelector))
        {
            decimal gross = 0m, net = 0m, fees = 0m;
            foreach (var t in group)
            {
                gross += t.PnL + t.TotalFeesPaid - t.NetFunding;
                net += t.PnL;
                fees += t.TotalFeesPaid;
            }
            double leakage = gross > 0 ? (double)(fees / gross) * 100.0 : 0.0;
            result[group.Key] = new TcaBucket(group.Count(), gross, net, fees, leakage);
        }
        return result;
    }
}

/// <summary>TCA attribution report.</summary>
/// <param name="GrossPnL">PnL before explicit and implicit costs.</param>
/// <param name="NetPnL">Realised PnL after commission; matches trade.PnL sum.</param>
/// <param name="TotalFees">Sum of explicit commission across all trades.</param>
/// <param name="NetFunding">Sum of funding cashflow (perp). Positive = received.</param>
/// <param name="ImplicitSlippageCost">Modelled slippage × trade count.</param>
/// <param name="CostLeakagePercent">(fees + slippage + negative funding) / grossPnL × 100.</param>
public sealed record TcaReport(
    int TradeCount,
    decimal GrossPnL,
    decimal NetPnL,
    decimal TotalFees,
    decimal NetFunding,
    decimal ImplicitSlippageCost,
    double CostLeakagePercent,
    Dictionary<string, TcaBucket> ByStrategy,
    Dictionary<string, TcaBucket> ByRegime,
    Dictionary<string, TcaBucket> ByExitPolicy);

public sealed record TcaBucket(
    int TradeCount,
    decimal GrossPnL,
    decimal NetPnL,
    decimal Fees,
    double CostLeakagePercent);
