using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

public sealed class HftExecutionService
{
    private readonly IExchangeService _exchangeService;
    private readonly ILogger<HftExecutionService> _logger;
    private readonly MarketExecutionProfileService _executionProfiles;

    public HftExecutionService(
        IExchangeService exchangeService,
        ILogger<HftExecutionService> logger,
        MarketExecutionProfileService? executionProfiles = null)
    {
        _exchangeService = exchangeService;
        _logger = logger;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
    }

    public async Task<Trade> ExecuteAsync(Signal signal, CancellationToken cancellationToken = default)
    {
        var market = ResolveMarket(signal.Symbol);
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        var projectedLatencyMs = EstimateProjectedLatencyMs(signal, profile);
        var projectedSlippagePercent = EstimateProjectedSlippagePercent(signal, profile);
        var sessionAllowed = IsPreferredHftWindow(market, signal.Timestamp);
        if (!sessionAllowed)
        {
            _logger.LogWarning("HFT execution blocked for {Symbol}: outside preferred HFT window.", signal.Symbol);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        if (signal.Volume24h > 0 && signal.Volume24h < profile.MinHftVolume24h)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: liquidity too weak ({Volume24h:N0}, needs {RequiredVolume:N0}) on {Venue}.",
                signal.Symbol,
                signal.Volume24h,
                profile.MinHftVolume24h,
                profile.Venue);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        if (projectedLatencyMs > profile.PreferredMaxLatencyMs * 1.35)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: projected latency {ProjectedLatency:F0}ms exceeds venue budget {LatencyBudget}ms.",
                signal.Symbol,
                projectedLatencyMs,
                profile.PreferredMaxLatencyMs);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        if (projectedSlippagePercent > profile.SlippagePercent * 1.75m)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: projected slippage {ProjectedSlippage:F4}% exceeds guardrail {SlippageBudget:F4}%.",
                signal.Symbol,
                projectedSlippagePercent,
                profile.SlippagePercent * 1.75m);
            return new Trade
            {
                SignalId = signal.Id,
                Symbol = signal.Symbol,
                Side = signal.Side,
                EntryPrice = signal.Price,
                Quantity = 0,
                Status = TradeStatus.Failed,
                StrategyTag = signal.StrategyTag,
                RegimeTag = signal.RegimeTag,
                ExecutionMode = "REAL",
                PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe"),
                EntryTime = DateTime.UtcNow
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var trade = await _exchangeService.ExecuteOrderAsync(signal);
        stopwatch.Stop();

        trade.ExecutionMode = "REAL";
        trade.PromotionStage = signal.Metadata.GetValueOrDefault("PromotionStage", "LiveProbe");
        trade.ExecutionLatencyMs = stopwatch.ElapsedMilliseconds;
        trade.EntrySlippagePercent = signal.Price > 0
            ? Math.Abs((trade.EntryPrice - signal.Price) / signal.Price) * 100m
            : 0m;

        _logger.LogInformation(
            "HFT execution completed for {Symbol} via {Venue}. Latency={Latency}ms, Slippage={Slippage:F4}%",
            signal.Symbol,
            profile.Venue,
            trade.ExecutionLatencyMs,
            trade.EntrySlippagePercent);

        return trade;
    }

    private static string ResolveMarket(string symbol)
    {
        if (symbol.Contains("-", StringComparison.OrdinalIgnoreCase)) return "Crypto";
        if (symbol.Contains("=", StringComparison.OrdinalIgnoreCase)) return "Forex";
        return "BIST";
    }

    private static bool IsPreferredHftWindow(string market, DateTime timestamp)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");

        return turkeyTime.Hour is >= 10 and < 13 or >= 15 and < 18;
    }

    private static double EstimateProjectedLatencyMs(Signal signal, MarketExecutionProfile profile)
    {
        var confidencePenalty = Math.Max(0, 80 - signal.Confidence) * 0.8;
        var volumeRatio = signal.Volume24h <= 0 || profile.MinHftVolume24h <= 0
            ? 1m
            : signal.Volume24h / profile.MinHftVolume24h;
        var liquidityPenalty = volumeRatio >= 2m ? 0 : (double)((2m - volumeRatio) * 35m);
        return profile.PreferredMaxLatencyMs + confidencePenalty + liquidityPenalty;
    }

    private static decimal EstimateProjectedSlippagePercent(Signal signal, MarketExecutionProfile profile)
    {
        var volumeRatio = signal.Volume24h <= 0 || profile.MinHftVolume24h <= 0
            ? 1m
            : signal.Volume24h / profile.MinHftVolume24h;
        var multiplier = volumeRatio >= 2m ? 0.85m :
            volumeRatio >= 1.25m ? 1.00m :
            volumeRatio >= 1m ? 1.35m : 1.80m;
        return profile.SlippagePercent * multiplier;
    }
}
