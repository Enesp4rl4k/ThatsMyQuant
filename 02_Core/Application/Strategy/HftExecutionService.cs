using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

public sealed class HftExecutionService
{
    /// <summary>Latency guard: reject if projected latency exceeds venue budget by this factor.</summary>
    private const double LatencyGuardMultiplier = 1.35;
    /// <summary>Slippage guard: reject if projected slippage exceeds profile slippage by this factor.</summary>
    private const decimal SlippageGuardMultiplier = 1.75m;

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
        var microstructure = ExtractMicrostructure(signal);
        var projectedLatencyMs = EstimateProjectedLatencyMs(microstructure, profile);
        var projectedSlippagePercent = EstimateProjectedSlippagePercent(microstructure, profile);
        var sessionAllowed = IsPreferredHftWindow(market, signal.Timestamp);

        if (!sessionAllowed)
        {
            _logger.LogWarning("HFT execution blocked for {Symbol}: outside preferred HFT window.", signal.Symbol);
            return BuildFailedTrade(signal);
        }

        if (signal.Volume24h > 0 && signal.Volume24h < profile.MinHftVolume24h)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: liquidity too weak ({Volume24h:N0}, needs {RequiredVolume:N0}) on {Venue}.",
                signal.Symbol, signal.Volume24h, profile.MinHftVolume24h, profile.Venue);
            return BuildFailedTrade(signal);
        }

        // Microstructure guard: spread too wide for HFT
        if (microstructure.SpreadBps > 0 && microstructure.SpreadBps > (double)(profile.SlippagePercent * 100))
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: spread {SpreadBps:F1}bps exceeds slippage budget.",
                signal.Symbol, microstructure.SpreadBps);
            return BuildFailedTrade(signal);
        }

        if (projectedLatencyMs > profile.PreferredMaxLatencyMs * LatencyGuardMultiplier)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: projected latency {ProjectedLatency:F0}ms exceeds venue budget {LatencyBudget}ms.",
                signal.Symbol, projectedLatencyMs, profile.PreferredMaxLatencyMs);
            return BuildFailedTrade(signal);
        }

        if (projectedSlippagePercent > profile.SlippagePercent * SlippageGuardMultiplier)
        {
            _logger.LogWarning(
                "HFT execution blocked for {Symbol}: projected slippage {ProjectedSlippage:F4}% exceeds guardrail {SlippageBudget:F4}%.",
                signal.Symbol, projectedSlippagePercent, profile.SlippagePercent * SlippageGuardMultiplier);
            return BuildFailedTrade(signal);
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
            "HFT execution completed for {Symbol} via {Venue}. Latency={Latency}ms, Slippage={Slippage:F4}%, Spread={Spread:F1}bps, OFI={OFI:F2}",
            signal.Symbol, profile.Venue, trade.ExecutionLatencyMs, trade.EntrySlippagePercent,
            microstructure.SpreadBps, microstructure.OfiStrength);

        return trade;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Microstructure Feature Extraction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts real microstructure features from the signal's Level-2 data.
    /// Falls back to conservative estimates when order book data is missing.
    /// </summary>
    internal static MicrostructureFeatures ExtractMicrostructure(Signal signal)
    {
        double spreadBps = 0;
        double midPrice = (double)signal.Price;

        if (signal.BestBid.HasValue && signal.BestAsk.HasValue
            && signal.BestBid.Value > 0 && signal.BestAsk.Value > 0)
        {
            midPrice = (signal.BestBid.Value + signal.BestAsk.Value) / 2.0;
            spreadBps = midPrice > 0
                ? (signal.BestAsk.Value - signal.BestBid.Value) / midPrice * 10_000
                : 0;
        }

        double ofiStrength = signal.OrderFlowImbalance ?? 0;

        // Volume-based liquidity score: 1.0 = adequate liquidity, <1.0 = thin
        double liquidityScore = 1.0;
        if (signal.Volume24h > 0)
        {
            // Normalize: 500k = 1.0 (adequate), below = proportionally worse
            liquidityScore = Math.Min(2.0, (double)signal.Volume24h / 500_000.0);
        }

        return new MicrostructureFeatures
        {
            SpreadBps = spreadBps,
            MidPrice = midPrice,
            OfiStrength = ofiStrength,
            LiquidityScore = liquidityScore,
            HasL2Data = signal.BestBid.HasValue && signal.BestAsk.HasValue
        };
    }

    /// <summary>
    /// Estimates projected latency using microstructure features instead of confidence heuristics.
    /// Latency penalty = f(spread_width, liquidity, OFI_adverse_selection).
    /// </summary>
    internal static double EstimateProjectedLatencyMs(MicrostructureFeatures micro, MarketExecutionProfile profile)
    {
        double baseLatency = profile.PreferredMaxLatencyMs;

        // Spread penalty: wider spread → more adverse selection risk → need faster execution
        // Every 5bps of spread adds 10ms of effective latency (queue degradation)
        double spreadPenalty = micro.SpreadBps > 0 ? micro.SpreadBps / 5.0 * 10.0 : 0;

        // Liquidity penalty: thin markets have wider effective spreads and slower fills
        double liquidityPenalty = micro.LiquidityScore >= 1.0
            ? 0
            : (1.0 - micro.LiquidityScore) * 50.0; // Up to 50ms for very thin markets

        // OFI adverse selection: if we're trading against strong flow, fills will be worse
        double ofiPenalty = 0;
        if (Math.Abs(micro.OfiStrength) > 0.5)
        {
            // Trading into strong adverse flow adds latency (queue position degrades)
            ofiPenalty = Math.Abs(micro.OfiStrength) * 20.0;
        }

        return baseLatency + spreadPenalty + liquidityPenalty + ofiPenalty;
    }

    /// <summary>
    /// Estimates projected slippage using microstructure features.
    /// Slippage = f(spread, participation_rate, OFI_alignment).
    /// </summary>
    internal static decimal EstimateProjectedSlippagePercent(MicrostructureFeatures micro, MarketExecutionProfile profile)
    {
        // Base: half-spread is the minimum cost of crossing
        decimal halfSpreadPct = micro.HasL2Data
            ? (decimal)(micro.SpreadBps / 10_000.0 / 2.0) * 100m  // Convert bps to %
            : profile.SlippagePercent; // Fallback to profile if no L2 data

        // Liquidity multiplier: thin markets have more impact
        decimal liquidityMultiplier = micro.LiquidityScore >= 1.5 ? 0.85m  // Very liquid: less slip
            : micro.LiquidityScore >= 1.0 ? 1.00m                         // Normal
            : micro.LiquidityScore >= 0.5 ? 1.35m                         // Thin
            : 1.80m;                                                       // Very thin

        // OFI alignment bonus/penalty:
        // Trading WITH the flow = better fills (aggressive side is thinner)
        // Trading AGAINST = worse fills (we're providing liquidity to the flow)
        decimal ofiAdjustment = 1.0m;
        if (Math.Abs(micro.OfiStrength) > 0.3)
        {
            // Positive adjustment means worse slippage
            ofiAdjustment = 1.0m + (decimal)(Math.Abs(micro.OfiStrength) * 0.2);
        }

        return halfSpreadPct * liquidityMultiplier * ofiAdjustment;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static string ResolveMarket(string symbol)
    {
        if (symbol.Contains("-", StringComparison.OrdinalIgnoreCase)) return "Crypto";
        if (symbol.Contains("=", StringComparison.OrdinalIgnoreCase)) return "Forex";
        return "BIST";
    }

    private static readonly TimeZoneInfo TurkeyTimeZone = ResolveTurkeyTimeZone();

    private static TimeZoneInfo ResolveTurkeyTimeZone()
    {
        // Windows uses "Turkey Standard Time", Linux/macOS use IANA "Europe/Istanbul"
        try { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
    }

    private static bool IsPreferredHftWindow(string market, DateTime timestamp)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
            return true;

        var utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        var turkeyTime = TimeZoneInfo.ConvertTimeFromUtc(utc, TurkeyTimeZone);

        return turkeyTime.Hour is >= 10 and < 13 or >= 15 and < 18;
    }

    private static Trade BuildFailedTrade(Signal signal) => new()
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

/// <summary>
/// Microstructure features extracted from Level-2 market data.
/// Used by HFT execution to make data-driven latency and slippage estimates
/// instead of confidence-based heuristics.
/// </summary>
public record struct MicrostructureFeatures
{
    /// <summary>Bid-ask spread in basis points.</summary>
    public double SpreadBps { get; init; }

    /// <summary>Mid-price from best bid/ask.</summary>
    public double MidPrice { get; init; }

    /// <summary>Order flow imbalance: -1.0 (all sellers) to +1.0 (all buyers).</summary>
    public double OfiStrength { get; init; }

    /// <summary>Liquidity score: 1.0 = adequate, &lt;1.0 = thin, &gt;1.0 = deep.</summary>
    public double LiquidityScore { get; init; }

    /// <summary>Whether real Level-2 data (bid/ask) is available.</summary>
    public bool HasL2Data { get; init; }
}
