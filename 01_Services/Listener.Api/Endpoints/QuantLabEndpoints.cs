using Microsoft.AspNetCore.Mvc;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace Listener.Api.Endpoints;

public static class QuantLabEndpoints
{
    private const int RollingWindow = 20;
    private const int DistributionBins = 11;

    public static IEndpointRouteBuilder MapQuantLabEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/quant-lab").WithTags("QuantLab");

        group.MapGet("/metrics", GetMetrics);
        group.MapGet("/strategies", GetStrategies);
        group.MapPost("/single-backtest", RunSingleBacktest);
        group.MapPost("/walk-forward", RunWalkForward);

        return endpoints;
    }

    /// <summary>
    /// Run a single-symbol backtest with a specified market type, timeframe,
    /// and starting capital. Returns the full BacktestResult — Sharpe, PF,
    /// equity curve, regime buckets, etc.
    /// </summary>
    private static async Task<IResult> RunSingleBacktest(
        [FromBody] SingleBacktestRequest req,
        IMarketDataService marketData,
        BacktestEngineApp engine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol))
            return Results.BadRequest(new { error = "symbol is required" });

        var symbol = req.Symbol.ToUpperInvariant();
        var timeframe = string.IsNullOrWhiteSpace(req.Timeframe) ? "1h" : req.Timeframe;
        var days = req.Days <= 0 ? 365 : Math.Clamp(req.Days, 30, 1095);
        var capital = req.StartingCapital <= 0 ? 100_000m : req.StartingCapital;
        var leverage = Math.Clamp(req.Leverage <= 0 ? 1m : req.Leverage, 1m, 15m);

        // Compute candle count needed
        var minutesPerBar = timeframe switch
        {
            "1m" => 1,
            "5m" => 5,
            "15m" => 15,
            "1h" => 60,
            "4h" => 240,
            "1d" => 1440,
            _ => 60
        };
        var totalMinutes = days * 24 * 60;
        var count = (int)Math.Ceiling((double)totalMinutes / minutesPerBar) + 200;
        count = Math.Clamp(count, 100, 5000);

        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, count, "Crypto", timeframe);
            if (candles is null || candles.Count < 100)
                return Results.Ok(new
                {
                    error = "insufficient candle history",
                    requested = count,
                    received = candles?.Count ?? 0,
                });

            var result = await engine.RunAsync(candles, symbol, timeframe, capital * leverage);

            return Results.Ok(new SingleBacktestResultDto(
                Symbol: symbol,
                Timeframe: timeframe,
                Days: days,
                CandleCount: candles.Count,
                Capital: capital,
                Leverage: leverage,
                TotalTrades: result.TotalTrades,
                WinningTrades: result.WinningTrades,
                LosingTrades: result.LosingTrades,
                WinRate: result.TotalTrades > 0 ? (double)result.WinningTrades / result.TotalTrades : 0,
                TotalPnL: result.TotalPnL,
                MaxDrawdownPercent: result.MaxDrawdownPercent,
                Sharpe: result.SharpeRatio,
                Sortino: result.SortinoRatio,
                ProfitFactor: result.ProfitFactor,
                AverageRR: result.AverageRR,
                FinalEquity: result.FinalEquity,
                StartDate: result.StartDate,
                EndDate: result.EndDate,
                EquityCurve: result.EquityCurve.Select(v => (double)v).ToArray(),
                Reasoning: result.Reasoning,
                EntryRejectReasons: result.EntryRejectReasons));
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Backtest failed", detail: ex.Message, statusCode: 500);
        }
    }

    /// <summary>
    /// Walk-forward analysis with rolling train/test splits. Validates
    /// strategy robustness — Walk-Forward Efficiency (WFE) > 0.5 is robust,
    /// < 0.3 likely overfit.
    /// </summary>
    private static async Task<IResult> RunWalkForward(
        [FromBody] WalkForwardRequest req,
        IMarketDataService marketData,
        WalkForwardEngine wf,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol))
            return Results.BadRequest(new { error = "symbol is required" });

        var symbol = req.Symbol.ToUpperInvariant();
        var timeframe = string.IsNullOrWhiteSpace(req.Timeframe) ? "1h" : req.Timeframe;
        var days = req.Days <= 0 ? 365 : Math.Clamp(req.Days, 60, 1095);
        var windows = Math.Clamp(req.NumWindows <= 0 ? 6 : req.NumWindows, 3, 12);
        var capital = req.Capital <= 0 ? 100_000m : req.Capital;

        var minutesPerBar = timeframe switch
        {
            "1h" => 60,
            "4h" => 240,
            "1d" => 1440,
            "15m" => 15,
            "5m" => 5,
            _ => 60
        };
        var count = (int)Math.Ceiling((double)days * 24 * 60 / minutesPerBar) + 200;
        count = Math.Clamp(count, 200, 5000);

        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, count, "Crypto", timeframe);
            if (candles is null || candles.Count < 200)
                return Results.Ok(new
                {
                    error = "insufficient candle history for walk-forward",
                    requested = count,
                    received = candles?.Count ?? 0,
                });

            var result = await wf.RunAsync(candles, symbol, timeframe, windows, 0.7, capital, ct);

            return Results.Ok(new WalkForwardResultDto(
                Symbol: result.Symbol,
                Timeframe: timeframe,
                NumWindows: windows,
                CompletedWindows: result.CompletedWindows,
                TotalWindows: result.TotalWindows,
                CandleCount: candles.Count,
                WalkForwardEfficiency: result.WalkForwardEfficiency,
                AggregateTrainSharpe: result.AggregateTrainSharpe,
                AggregateOosSharpe: result.AggregateOosSharpe,
                AggregateOosPnL: result.AggregateOosPnL,
                AggregateOosWinRate: result.AggregateOosWinRate,
                OosConsistency: result.OosConsistency,
                RegimeStabilityScore: result.RegimeStabilityScore,
                ProfitFactorDegradation: result.ProfitFactorDegradation,
                IsRobust: result.IsRobust,
                Windows: result.WindowResults.Select(w => new WalkForwardWindowDto(
                    w.WindowIndex,
                    w.TrainStartDate,
                    w.TrainEndDate,
                    w.OosStartDate,
                    w.OosEndDate,
                    w.TrainSharpe,
                    w.OosSharpe,
                    w.TrainPnL,
                    w.OosPnL,
                    w.TrainTrades,
                    w.OosTrades,
                    w.TrainWinRate,
                    w.OosWinRate,
                    w.TrainProfitFactor,
                    w.OosProfitFactor,
                    w.TrainMaxDD,
                    w.OosMaxDD,
                    w.TrainRegime,
                    w.OosRegime)).ToArray()));
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Walk-forward failed", detail: ex.Message, statusCode: 500);
        }
    }

    private static async Task<IResult> GetStrategies(ITradeRepository trades)
    {
        var all = await trades.GetAllAsync(2000);
        var tags = all
            .Where(t => t.IsClosed)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.StrategyTag) ? "Unknown" : t.StrategyTag)
            .Select(g => new StrategyOptionDto(g.Key, g.Count()))
            .OrderByDescending(s => s.TradeCount)
            .ToList();

        return Results.Ok(tags);
    }

    private static async Task<IResult> GetMetrics(ITradeRepository trades, string? strategyTag, int? limit)
    {
        var fetchCount = Math.Clamp(limit ?? 500, 20, 2000);
        var all = await trades.GetAllAsync(fetchCount);

        var closed = all
            .Where(t => t.IsClosed)
            .Where(t => string.IsNullOrWhiteSpace(strategyTag) || string.Equals(t.StrategyTag, strategyTag, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.ExitTime ?? t.EntryTime)
            .ToList();

        if (closed.Count == 0)
        {
            return Results.Ok(QuantLabMetricsDto.Empty(strategyTag ?? "ALL"));
        }

        var pnls = closed.Select(t => (double)t.RealisedPnL).ToArray();
        var returns = closed.Select(t => (double)t.PnlPercent).ToArray();

        var rollingSharpe = ComputeRollingSharpe(returns, RollingWindow);
        var distribution = ComputeReturnDistribution(returns, DistributionBins);
        var (var95, cvar95) = ComputeVaR(returns, 0.05);
        var drawdown = ComputeDrawdownCurve(pnls);
        var mfeMae = closed.Select(t => new MfeMaeDotDto(
            (double)t.MaxFavorableExcursionPercent,
            (double)t.MaxAdverseExcursionPercent,
            (double)t.RealisedPnL,
            t.Symbol,
            t.ExitTime ?? t.EntryTime)).ToArray();

        var summary = new SummaryStatsDto(
            TradeCount: closed.Count,
            TotalPnL: closed.Sum(t => t.RealisedPnL),
            MeanReturn: returns.Average(),
            StdDevReturn: StdDev(returns),
            SharpeAllTime: SharpeRatio(returns),
            MaxDrawdown: drawdown.Length == 0 ? 0d : drawdown.Min(d => d.Drawdown),
            VaR95: var95,
            CVaR95: cvar95);

        return Results.Ok(new QuantLabMetricsDto(
            StrategyTag: strategyTag ?? "ALL",
            Summary: summary,
            RollingSharpe: rollingSharpe,
            ReturnDistribution: distribution,
            DrawdownCurve: drawdown,
            MfeMaeScatter: mfeMae));
    }

    private static RollingSharpePointDto[] ComputeRollingSharpe(double[] returns, int window)
    {
        if (returns.Length < window) return Array.Empty<RollingSharpePointDto>();
        var pts = new List<RollingSharpePointDto>(returns.Length - window + 1);
        for (int i = window - 1; i < returns.Length; i++)
        {
            double sum = 0, sumSq = 0;
            for (int j = i - window + 1; j <= i; j++)
            {
                sum += returns[j];
                sumSq += returns[j] * returns[j];
            }
            double mean = sum / window;
            double variance = (sumSq / window) - (mean * mean);
            double std = variance > 0 ? Math.Sqrt(variance) : 0;
            double sharpe = std > 0 ? (mean / std) * Math.Sqrt(252) : 0;
            pts.Add(new RollingSharpePointDto(i + 1, sharpe));
        }
        return pts.ToArray();
    }

    private static DistributionBinDto[] ComputeReturnDistribution(double[] returns, int bins)
    {
        if (returns.Length == 0) return Array.Empty<DistributionBinDto>();
        double min = returns.Min();
        double max = returns.Max();
        if (Math.Abs(max - min) < 1e-9)
        {
            return new[] { new DistributionBinDto(min, max, returns.Length) };
        }
        double width = (max - min) / bins;
        var counts = new int[bins];
        foreach (var r in returns)
        {
            int idx = (int)Math.Min(bins - 1, Math.Max(0, Math.Floor((r - min) / width)));
            counts[idx]++;
        }
        var result = new DistributionBinDto[bins];
        for (int i = 0; i < bins; i++)
        {
            result[i] = new DistributionBinDto(
                BinStart: min + width * i,
                BinEnd: min + width * (i + 1),
                Count: counts[i]);
        }
        return result;
    }

    private static (double var, double cvar) ComputeVaR(double[] returns, double alpha)
    {
        if (returns.Length == 0) return (0, 0);
        var sorted = returns.OrderBy(r => r).ToArray();
        int idx = Math.Max(0, (int)Math.Floor(alpha * sorted.Length) - 1);
        double var = sorted[idx];
        double cvar = sorted.Take(Math.Max(1, idx + 1)).Average();
        return (var, cvar);
    }

    private static DrawdownPointDto[] ComputeDrawdownCurve(double[] pnls)
    {
        if (pnls.Length == 0) return Array.Empty<DrawdownPointDto>();
        var equity = 0d;
        var peak = 0d;
        var pts = new DrawdownPointDto[pnls.Length];
        for (int i = 0; i < pnls.Length; i++)
        {
            equity += pnls[i];
            peak = Math.Max(peak, equity);
            double dd = peak == 0 ? 0 : (equity - peak) / Math.Max(1d, Math.Abs(peak));
            pts[i] = new DrawdownPointDto(i + 1, equity, dd);
        }
        return pts;
    }

    private static double StdDev(double[] xs)
    {
        if (xs.Length < 2) return 0;
        double mean = xs.Average();
        double sumSq = 0;
        foreach (var x in xs) sumSq += (x - mean) * (x - mean);
        return Math.Sqrt(sumSq / (xs.Length - 1));
    }

    private static double SharpeRatio(double[] returns)
    {
        if (returns.Length < 2) return 0;
        double mean = returns.Average();
        double std = StdDev(returns);
        return std > 0 ? (mean / std) * Math.Sqrt(252) : 0;
    }
}

public sealed record QuantLabMetricsDto(
    string StrategyTag,
    SummaryStatsDto Summary,
    RollingSharpePointDto[] RollingSharpe,
    DistributionBinDto[] ReturnDistribution,
    DrawdownPointDto[] DrawdownCurve,
    MfeMaeDotDto[] MfeMaeScatter)
{
    public static QuantLabMetricsDto Empty(string tag) => new(
        tag,
        new SummaryStatsDto(0, 0m, 0, 0, 0, 0, 0, 0),
        Array.Empty<RollingSharpePointDto>(),
        Array.Empty<DistributionBinDto>(),
        Array.Empty<DrawdownPointDto>(),
        Array.Empty<MfeMaeDotDto>());
}

public sealed record SummaryStatsDto(
    int TradeCount,
    decimal TotalPnL,
    double MeanReturn,
    double StdDevReturn,
    double SharpeAllTime,
    double MaxDrawdown,
    double VaR95,
    double CVaR95);

public sealed record RollingSharpePointDto(int TradeIndex, double Sharpe);
public sealed record DistributionBinDto(double BinStart, double BinEnd, int Count);
public sealed record DrawdownPointDto(int TradeIndex, double CumulativePnL, double Drawdown);
public sealed record MfeMaeDotDto(double Mfe, double Mae, double Pnl, string Symbol, DateTime ExitTime);
public sealed record StrategyOptionDto(string StrategyTag, int TradeCount);

public sealed record SingleBacktestRequest(
    string Symbol,
    string? Timeframe,
    int Days,
    decimal StartingCapital,
    decimal Leverage);

public sealed record SingleBacktestResultDto(
    string Symbol,
    string Timeframe,
    int Days,
    int CandleCount,
    decimal Capital,
    decimal Leverage,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    double WinRate,
    decimal TotalPnL,
    decimal MaxDrawdownPercent,
    double Sharpe,
    double Sortino,
    double ProfitFactor,
    double AverageRR,
    decimal FinalEquity,
    DateTime StartDate,
    DateTime EndDate,
    double[] EquityCurve,
    string Reasoning,
    Dictionary<string, int> EntryRejectReasons);

public sealed record WalkForwardRequest(
    string Symbol,
    string? Timeframe,
    int Days,
    int NumWindows,
    decimal Capital);

public sealed record WalkForwardResultDto(
    string Symbol,
    string Timeframe,
    int NumWindows,
    int CompletedWindows,
    int TotalWindows,
    int CandleCount,
    double WalkForwardEfficiency,
    double AggregateTrainSharpe,
    double AggregateOosSharpe,
    decimal AggregateOosPnL,
    double AggregateOosWinRate,
    double OosConsistency,
    double RegimeStabilityScore,
    double ProfitFactorDegradation,
    bool IsRobust,
    WalkForwardWindowDto[] Windows);

public sealed record WalkForwardWindowDto(
    int WindowIndex,
    DateTime TrainStartDate,
    DateTime TrainEndDate,
    DateTime OosStartDate,
    DateTime OosEndDate,
    double TrainSharpe,
    double OosSharpe,
    decimal TrainPnL,
    decimal OosPnL,
    int TrainTrades,
    int OosTrades,
    double TrainWinRate,
    double OosWinRate,
    double TrainProfitFactor,
    double OosProfitFactor,
    double TrainMaxDD,
    double OosMaxDD,
    string TrainRegime,
    string OosRegime);
