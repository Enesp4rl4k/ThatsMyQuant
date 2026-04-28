using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Endpoints;

/// <summary>
/// Promotion readiness — for each strategy tag with at least one closed paper trade,
/// synthesises a <see cref="BacktestResult"/> from the Trade history and runs the
/// FormalPromotionGate Paper→Live evaluation. Returns the per-criterion pass/fail
/// surface for the dashboard's Promotion Radar card.
/// </summary>
public static class PromotionEndpoints
{
    public static IEndpointRouteBuilder MapPromotionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/promotion").WithTags("Promotion");
        group.MapGet("/readiness", GetReadiness);
        endpoints.MapGet("/api/ml/status", GetMlStatus).WithTags("MachineLearning");
        return endpoints;
    }

    private static IResult GetMlStatus(ModelTrainingPipeline pipeline)
    {
        var report = pipeline.GetDatasetReport();
        return Results.Ok(new MlStatusDto(
            HasActiveModel: pipeline.HasActiveModel,
            ArtifactPath: pipeline.ArtifactPath,
            TotalSamples: report.TotalSamples,
            PositiveSamples: report.PositiveSamples,
            NegativeSamples: report.NegativeSamples,
            ClassBalance: report.ClassBalance,
            IsTrainable: report.IsTrainable,
            UniqueSymbols: report.UniqueSymbols,
            EarliestSampleUtc: report.DateRange.Start,
            LatestSampleUtc: report.DateRange.End,
            DirectionalStrategyTags: TradeOutcomeMlSampler.DirectionalStrategyTags.ToArray()));
    }

    private static async Task<IResult> GetReadiness(
        ITradeRepository trades,
        FormalPromotionGate gate,
        ILogger<FormalPromotionGate> logger)
    {
        var all = await trades.GetAllAsync(5000);
        var closed = all.Where(t => t.IsClosed && t.ExitTime.HasValue).ToList();
        if (closed.Count == 0)
        {
            return Results.Ok(new ReadinessResponse(
                Thresholds: ToDto(PromotionThresholds.DefaultPaperToLive),
                Strategies: Array.Empty<StrategyReadinessDto>()));
        }

        var byStrategy = closed
            .GroupBy(t => string.IsNullOrWhiteSpace(t.StrategyTag) ? "Untagged" : t.StrategyTag)
            .OrderByDescending(g => g.Count())
            .ToList();

        var results = new List<StrategyReadinessDto>();

        foreach (var grp in byStrategy)
        {
            var tradeList = grp.OrderBy(t => t.ExitTime!.Value).ToList();
            var synth = SynthesizeBacktestResult(tradeList);

            // Use Paper→Live thresholds — most actionable for active paper period.
            var thresholds = PromotionThresholds.DefaultPaperToLive;
            var criteria = EvaluateCriteria(synth, thresholds);
            var passCount = criteria.Count(c => c.Passed);

            results.Add(new StrategyReadinessDto(
                StrategyTag: grp.Key,
                Stage: "PaperToLive",
                Approved: passCount == criteria.Count,
                PassCount: passCount,
                TotalCount: criteria.Count,
                ReadinessScore: criteria.Count == 0 ? 0d : (double)passCount / criteria.Count,
                Trades: synth.TotalTrades,
                Sharpe: synth.SharpeRatio,
                ProfitFactor: synth.ProfitFactor,
                WinRate: synth.WinRate,
                MaxDrawdownPercent: (double)synth.MaxDrawdownPercent,
                AverageRR: synth.AverageRR,
                CostLeakagePercent: synth.CostLeakagePercent,
                NetPnL: synth.TotalPnL,
                Criteria: criteria));
        }

        return Results.Ok(new ReadinessResponse(
            Thresholds: ToDto(PromotionThresholds.DefaultPaperToLive),
            Strategies: results));
    }

    /// <summary>
    /// Reconstructs the BacktestResult shape from a strategy's closed Trades so that
    /// the FormalPromotionGate logic doesn't need a separate "paper-trades" overload.
    /// </summary>
    private static BacktestResult SynthesizeBacktestResult(IReadOnlyList<Trade> trades)
    {
        var winners = trades.Where(t => t.RealisedPnL > 0m).ToList();
        var losers = trades.Where(t => t.RealisedPnL < 0m).ToList();

        var totalPnl = trades.Sum(t => t.RealisedPnL);
        var grossProfit = winners.Sum(t => t.RealisedPnL);
        var grossLoss = Math.Abs(losers.Sum(t => t.RealisedPnL));
        var pf = grossLoss <= 0m ? (grossProfit > 0m ? 99.0 : 0.0) : (double)(grossProfit / grossLoss);

        // Sharpe: per-trade PnL% — annualised assuming ~252 effective bars.
        var rets = trades.Select(t => (double)t.PnlPercent).ToArray();
        var mean = rets.Length == 0 ? 0d : rets.Average();
        var variance = rets.Length <= 1 ? 0d : rets.Select(r => (r - mean) * (r - mean)).Sum() / (rets.Length - 1);
        var stdev = Math.Sqrt(variance);
        var sharpe = stdev <= 1e-9 ? 0d : mean / stdev * Math.Sqrt(252);

        // Sortino: downside-only stdev.
        var negRets = rets.Where(r => r < 0).ToArray();
        var downVar = negRets.Length == 0 ? 0d : negRets.Select(r => r * r).Sum() / negRets.Length;
        var downStd = Math.Sqrt(downVar);
        var sortino = downStd <= 1e-9 ? 0d : mean / downStd * Math.Sqrt(252);

        // MaxDD from per-trade equity curve.
        decimal cum = 0m, peak = 0m, maxDdAbs = 0m;
        foreach (var t in trades)
        {
            cum += t.RealisedPnL;
            if (cum > peak) peak = cum;
            var dd = peak - cum;
            if (dd > maxDdAbs) maxDdAbs = dd;
        }
        var basis = peak > 0m ? peak : Math.Abs(trades.FirstOrDefault()?.EntryPrice * trades.FirstOrDefault()?.Quantity ?? 1m);
        var maxDdPct = basis <= 0m ? 0m : maxDdAbs / basis * 100m;

        var avgWin = winners.Count > 0 ? winners.Average(t => Math.Abs(t.RealisedPnL)) : 0m;
        var avgLoss = losers.Count > 0 ? losers.Average(t => Math.Abs(t.RealisedPnL)) : 0m;
        var avgRr = avgLoss <= 0m ? 0d : (double)(avgWin / avgLoss);

        var fees = trades.Sum(t => t.TotalFeesPaid);
        var grossAbs = grossProfit + grossLoss;
        var costLeakPct = grossAbs <= 0m ? 0d : (double)(fees / grossAbs) * 100d;

        return new BacktestResult
        {
            Symbol = trades[0].Symbol,
            TotalTrades = trades.Count,
            WinningTrades = winners.Count,
            LosingTrades = losers.Count,
            TotalPnL = totalPnl,
            MaxDrawdown = maxDdAbs,
            MaxDrawdownPercent = maxDdPct,
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            AverageRR = avgRr,
            ProfitFactor = pf,
            StartingCapital = 0m,
            FinalEquity = totalPnl,
            StartDate = trades[0].EntryTime,
            EndDate = trades[^1].ExitTime ?? trades[^1].EntryTime,
            TotalFeesPaid = fees,
            GrossPnL = grossProfit - grossLoss,
            CostLeakagePercent = costLeakPct,
        };
    }

    private static List<CriterionDto> EvaluateCriteria(BacktestResult r, PromotionThresholds t) =>
        new()
        {
            new("Trades", r.TotalTrades, t.MinTrades, r.TotalTrades >= t.MinTrades, "min"),
            new("Sharpe", r.SharpeRatio, t.MinSharpe, r.SharpeRatio >= t.MinSharpe, "min"),
            new("ProfitFactor", r.ProfitFactor, t.MinProfitFactor, r.ProfitFactor >= t.MinProfitFactor, "min"),
            new("MaxDrawdown%", (double)r.MaxDrawdownPercent, (double)t.MaxDrawdownPct, r.MaxDrawdownPercent <= t.MaxDrawdownPct, "max"),
            new("WinRate%", r.WinRate, t.MinWinRate, r.WinRate >= t.MinWinRate, "min"),
            new("AvgRR", r.AverageRR, t.MinAvgRR, r.AverageRR >= t.MinAvgRR, "min"),
            new("CostLeakage%", r.CostLeakagePercent, t.MaxCostLeakagePct, r.CostLeakagePercent <= t.MaxCostLeakagePct, "max"),
        };

    private static PromotionThresholdsDto ToDto(PromotionThresholds t) => new(
        MinTrades: t.MinTrades,
        MinSharpe: t.MinSharpe,
        MinProfitFactor: t.MinProfitFactor,
        MaxDrawdownPct: (double)t.MaxDrawdownPct,
        MinWinRate: t.MinWinRate,
        MinAvgRR: t.MinAvgRR,
        MaxCostLeakagePct: t.MaxCostLeakagePct);
}

public sealed record ReadinessResponse(
    PromotionThresholdsDto Thresholds,
    IReadOnlyList<StrategyReadinessDto> Strategies);

public sealed record StrategyReadinessDto(
    string StrategyTag,
    string Stage,
    bool Approved,
    int PassCount,
    int TotalCount,
    double ReadinessScore,
    int Trades,
    double Sharpe,
    double ProfitFactor,
    double WinRate,
    double MaxDrawdownPercent,
    double AverageRR,
    double CostLeakagePercent,
    decimal NetPnL,
    IReadOnlyList<CriterionDto> Criteria);

public sealed record CriterionDto(
    string Name,
    double Value,
    double Threshold,
    bool Passed,
    string Direction);

public sealed record MlStatusDto(
    bool HasActiveModel,
    string ArtifactPath,
    int TotalSamples,
    int PositiveSamples,
    int NegativeSamples,
    double ClassBalance,
    bool IsTrainable,
    int UniqueSymbols,
    DateTime EarliestSampleUtc,
    DateTime LatestSampleUtc,
    string[] DirectionalStrategyTags);

public sealed record PromotionThresholdsDto(
    int MinTrades,
    double MinSharpe,
    double MinProfitFactor,
    double MaxDrawdownPct,
    double MinWinRate,
    double MinAvgRR,
    double MaxCostLeakagePct);
