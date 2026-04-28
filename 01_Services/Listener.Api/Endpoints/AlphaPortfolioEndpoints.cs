using ThatsMyQuant.Application.Strategy;

namespace Listener.Api.Endpoints;

public static class AlphaPortfolioEndpoints
{
    private static readonly ValidatedPositionDto[] DefaultPortfolio =
    {
        new("TRX-XLM-Pair-4h",   "PairsArbitrage", "TRX-USDT", "XLM-USDT", "4h", 0.20m, 0.59, 4.89m, 0.75),
        new("SOL-AVAX-Pair-4h",  "PairsArbitrage", "SOL-USDT", "AVAX-USDT", "4h", 0.20m, 0.25, 1.23m, 0.55),
        new("SOL-LINK-Pair-4h",  "PairsArbitrage", "SOL-USDT", "LINK-USDT", "4h", 0.20m, 0.35, 1.63m, 0.50),
        new("LTC-Funding",       "FundingAlpha",   "LTC-USDT", null,        "1h", 0.15m, 1.12, 1.08m, 0.507),
        new("DOGE-Funding",      "FundingAlpha",   "DOGE-USDT", null,       "1h", 0.13m, 1.62, 1.12m, 0.534),
        new("AAVE-Funding",      "FundingAlpha",   "AAVE-USDT", null,       "1h", 0.12m, 0.99, 1.07m, 0.474),
    };

    public static IEndpointRouteBuilder MapAlphaPortfolioEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/alpha").WithTags("AlphaPortfolio");

        group.MapGet("/positions", () => Results.Ok(DefaultPortfolio));
        group.MapPost("/backtest", RunBacktest);

        return endpoints;
    }

    private static async Task<IResult> RunBacktest(
        BacktestRequest req,
        PortfolioBacktestRunner runner,
        ILogger<PortfolioBacktestRunner> logger,
        CancellationToken ct)
    {
        var positions = (req.Positions is { Length: > 0 } ? req.Positions : DefaultPortfolio).ToArray();
        var totalCapital = req.TotalCapital <= 0m ? 100_000m : req.TotalCapital;
        var days = req.BacktestDays <= 0 ? 365 : req.BacktestDays;
        var leverage = Math.Clamp(req.Leverage <= 0m ? 3m : req.Leverage, 1m, 15m);
        var weightingMode = string.Equals(req.WeightingMode, "InverseVolatility", StringComparison.OrdinalIgnoreCase)
            ? PortfolioWeightingMode.InverseVolatility
            : PortfolioWeightingMode.Fixed;

        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc.AddDays(-days);

        var fundingCfg = new FundingAlphaConfig(
            EntryThreshold: 0.0001m,
            TakeProfitPct: 0.02m,
            StopLossPct: 0.015m,
            MaxHoldingBars: 16);

        var configPositions = positions.Select(p => new PortfolioPosition(
            Name: p.Name,
            Type: string.Equals(p.Type, "FundingAlpha", StringComparison.OrdinalIgnoreCase)
                ? PortfolioPositionType.FundingAlpha
                : PortfolioPositionType.PairsArbitrage,
            SymbolA: p.SymbolA,
            SymbolB: p.SymbolB,
            Timeframe: p.Timeframe,
            CapitalWeight: p.Weight,
            FundingConfig: string.Equals(p.Type, "FundingAlpha", StringComparison.OrdinalIgnoreCase) ? fundingCfg : null,
            Leverage: leverage)).ToList();

        try
        {
            var result = await runner.RunAsync(configPositions, startUtc, endUtc, totalCapital, weightingMode, ct).ConfigureAwait(false);

            var corrSize = result.StrategyNames.Length;
            var flatCorr = new List<CorrelationCellDto>(corrSize * corrSize);
            double offSum = 0;
            int offCount = 0;
            for (int i = 0; i < corrSize; i++)
            {
                for (int j = 0; j < corrSize; j++)
                {
                    var v = result.CorrelationMatrix[i, j];
                    flatCorr.Add(new CorrelationCellDto(result.StrategyNames[i], result.StrategyNames[j], v));
                    if (i != j)
                    {
                        offSum += v;
                        offCount++;
                    }
                }
            }
            var avgCorrelation = offCount == 0 ? 0d : offSum / offCount;

            var legResults = result.StrategyResults.Select(s => new LegResultDto(
                Name: s.Name,
                Type: s.Type,
                Capital: s.Capital,
                Trades: s.Trades,
                WinRate: s.WinRate,
                NetPnl: s.NetPnl,
                Sharpe: s.SharpeRatio,
                MaxDrawdownPct: s.MaxDrawdownPct,
                ProfitFactor: s.ProfitFactor)).ToList();

            return Results.Ok(new BacktestResponseDto(
                TotalCapital: result.TotalCapital,
                FinalEquity: result.FinalEquity,
                TotalPnl: result.TotalPnl,
                TotalPnlPct: result.TotalPnlPct,
                PortfolioSharpe: result.PortfolioSharpe,
                MaxDrawdown: result.MaxDrawdown,
                AverageCorrelation: avgCorrelation,
                TradingDays: result.TradingDays,
                StrategyNames: result.StrategyNames,
                EquityCurve: result.EquityCurve,
                Dates: result.Dates,
                Legs: legResults,
                CorrelationMatrix: flatCorr));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Portfolio backtest failed");
            return Results.Problem(detail: ex.Message, statusCode: 500);
        }
    }
}

public sealed record ValidatedPositionDto(
    string Name,
    string Type,
    string SymbolA,
    string? SymbolB,
    string Timeframe,
    decimal Weight,
    double ExpectedSharpe,
    decimal ExpectedProfitFactor,
    double ExpectedWinRate);

public sealed record BacktestRequest(
    ValidatedPositionDto[]? Positions,
    decimal TotalCapital,
    int BacktestDays,
    decimal Leverage,
    string? WeightingMode);

public sealed record BacktestResponseDto(
    decimal TotalCapital,
    decimal FinalEquity,
    decimal TotalPnl,
    decimal TotalPnlPct,
    double PortfolioSharpe,
    decimal MaxDrawdown,
    double AverageCorrelation,
    int TradingDays,
    string[] StrategyNames,
    decimal[] EquityCurve,
    DateTime[] Dates,
    IReadOnlyList<LegResultDto> Legs,
    IReadOnlyList<CorrelationCellDto> CorrelationMatrix);

public sealed record LegResultDto(
    string Name,
    string Type,
    decimal Capital,
    int Trades,
    double WinRate,
    decimal NetPnl,
    double Sharpe,
    decimal MaxDrawdownPct,
    decimal ProfitFactor);

public sealed record CorrelationCellDto(string RowStrategy, string ColStrategy, double Value);
