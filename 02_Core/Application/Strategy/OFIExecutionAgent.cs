using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized execution agent that monitors Order Flow Imbalance (OFI).
/// Uses volatility-scaled thresholds and fill probability estimation:
/// - True Microstructure Order Placement (Anchors against BestBid/BestAsk)
/// - Volatility-scaled spread and OFI thresholds
/// - Anti-Spoofing decay logic with spread-adjusted wall age
/// - Fill probability gating to avoid adverse selection
/// </summary>
public partial class OFIExecutionAgent : INotificationHandler<Signal>
{
    private readonly ILogger<OFIExecutionAgent> _logger;
    private readonly IExchangeInfoProvider _exchangeInfo;

    // ── Configurable thresholds ──────────────────────────────────────────────
    private const double BaseOfiThreshold = 0.8;
    private const double BaseWallAgeMs = 50.0;
    private const double BaseSpreadMultiple = 2.0;
    private const double MinFillProbability = 0.55;

    public OFIExecutionAgent(ILogger<OFIExecutionAgent> logger, IExchangeInfoProvider exchangeInfo)
    {
        _logger = logger;
        _exchangeInfo = exchangeInfo;
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // Require native Level 2 execution telemetry to proceed
        if (!notification.OrderFlowImbalance.HasValue ||
            !notification.BestBid.HasValue ||
            !notification.BestAsk.HasValue)
            return Task.CompletedTask;

        double imbalance = notification.OrderFlowImbalance.Value;
        double wallAgeMs = notification.WallAgeMs ?? 0d;
        double tickSize = _exchangeInfo.GetTickSize(notification.Symbol);
        if (tickSize <= 0) tickSize = 0.01; // Fallback to safe default if exchange info unavailable
        double spread = notification.BestAsk.Value - notification.BestBid.Value;
        double midPrice = (notification.BestBid.Value + notification.BestAsk.Value) / 2.0;

        // Spread in tick-size units: 1.0 = tight (1 tick), >3.0 = wide
        double spreadTicks = spread / tickSize;

        // ── Volatility-scaled thresholds ─────────────────────────────────
        // In volatile/wide-spread conditions, require stronger OFI and longer wall persistence
        double volatilityScale = Math.Clamp(spreadTicks / 2.0, 0.5, 3.0);

        // 1. Anti-Spoofing Guard: wider spreads need longer wall persistence
        double requiredWallAge = BaseWallAgeMs * volatilityScale;
        if (wallAgeMs < requiredWallAge)
        {
            LogSpoofingFilter(_logger, notification.Symbol, wallAgeMs, requiredWallAge);
            return Task.CompletedTask;
        }

        // 2. Adverse Selection Guard: spread threshold scales with volatility
        double maxSpread = BaseSpreadMultiple * tickSize * Math.Max(1.0, volatilityScale * 0.8);
        if (spread > maxSpread)
        {
            LogWideSpread(_logger, notification.Symbol, spread, maxSpread);
            return Task.CompletedTask;
        }

        // 3. Volatility-scaled OFI threshold: tight markets = lower threshold, wide = higher
        double ofiThreshold = Math.Clamp(BaseOfiThreshold * (0.7 + 0.3 * volatilityScale), 0.6, 0.95);

        // 4. Fill probability estimation before committing
        double fillProb = EstimateFillProbability(spreadTicks, imbalance, wallAgeMs);
        if (fillProb < MinFillProbability)
        {
            LogLowFillProbability(_logger, notification.Symbol, fillProb, MinFillProbability);
            return Task.CompletedTask;
        }

        if (imbalance > ofiThreshold)
        {
            // True Microstructure Precision: Anchor explicitly to the Order Book BID level
            double frontRunPrice = notification.BestBid.Value + tickSize;

            notification.ExecutionAction = ExecutionActionType.LimitFrontRunBuy;
            notification.AddReason(SignalReasonFlags.OFIFrontRunBuy);

            LogBuyWallFrontRun(_logger, imbalance, frontRunPrice, fillProb, ofiThreshold);
        }
        else if (imbalance < -ofiThreshold)
        {
            // True Microstructure Precision: Anchor explicitly to the Order Book ASK level
            double frontRunPrice = notification.BestAsk.Value - tickSize;

            notification.ExecutionAction = ExecutionActionType.LimitFrontRunSell;
            notification.AddReason(SignalReasonFlags.OFIFrontRunSell);

            LogSellWallFrontRun(_logger, imbalance, frontRunPrice, fillProb, ofiThreshold);
        }

        return Task.CompletedTask;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Fill Probability Estimation
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Estimates the probability of a limit order fill based on microstructure features.
    /// P(fill) = f(spread_tightness, OFI_strength, wall_persistence).
    /// </summary>
    internal static double EstimateFillProbability(double spreadTicks, double imbalance, double wallAgeMs)
    {
        // Base: tight spread = high fill prob (1 tick = 0.85 base, 3+ ticks = 0.4 base)
        double spreadFactor = Math.Clamp(1.0 - (spreadTicks - 1.0) * 0.15, 0.3, 0.9);

        // OFI strength: stronger imbalance = higher confidence the wall is real
        double ofiFactor = Math.Clamp(Math.Abs(imbalance), 0.0, 1.0);

        // Wall persistence: older walls are more likely real (100ms+ is confident)
        double ageFactor = Math.Clamp(wallAgeMs / 150.0, 0.2, 1.0);

        // Weighted geometric-ish combination
        return spreadFactor * 0.4 + ofiFactor * 0.35 + ageFactor * 0.25;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  High-Performance Source Generator Logging (.NET 6+)
    // ═════════════════════════════════════════════════════════════════════════

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "[FRONT-RUN] Buy Wall detected (OFI={Imbalance:P2}, threshold={Threshold:F2}). Limit Buy at {FrontRunPrice}. FillProb={FillProb:P1}.")]
    private static partial void LogBuyWallFrontRun(ILogger logger, double imbalance, double frontRunPrice, double fillProb, double threshold);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "[FRONT-RUN] Sell Wall detected (OFI={Imbalance:P2}, threshold={Threshold:F2}). Limit Sell at {FrontRunPrice}. FillProb={FillProb:P1}.")]
    private static partial void LogSellWallFrontRun(ILogger logger, double imbalance, double frontRunPrice, double fillProb, double threshold);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "[OFI-FILTER] {Symbol}: spoofing guard — wall age {WallAge:F0}ms < required {RequiredAge:F0}ms.")]
    private static partial void LogSpoofingFilter(ILogger logger, string symbol, double wallAge, double requiredAge);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "[OFI-FILTER] {Symbol}: wide spread {Spread:F6} > max {MaxSpread:F6}.")]
    private static partial void LogWideSpread(ILogger logger, string symbol, double spread, double maxSpread);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "[OFI-FILTER] {Symbol}: fill probability {FillProb:P1} < minimum {MinProb:P1}.")]
    private static partial void LogLowFillProbability(ILogger logger, string symbol, double fillProb, double minProb);
}
