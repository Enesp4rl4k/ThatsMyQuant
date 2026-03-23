using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized execution agent that monitors Order Flow Imbalance (OFI).
/// Converted to a zero-allocation partial class utilizing:
/// - True Microstructure Order Placement (Anchors against BestBid/BestAsk)
/// - Protective Spread-Vacuum logic
/// - Anti-Spoofing decay logic (WallAgeMs)
/// - FPU Double-precision calculation matrix
/// </summary>
public partial class OFIExecutionAgent : INotificationHandler<Signal>
{
    private readonly ILogger<OFIExecutionAgent> _logger;
    private readonly IExchangeInfoProvider _exchangeInfo;

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

        // 1. Anti-Spoofing Guard: Ignore massive fake limit orders that haven't lived for > 50ms
        if (wallAgeMs < 50d) return Task.CompletedTask;

        double tickSize = _exchangeInfo.GetTickSize(notification.Symbol);
        double spread = notification.BestAsk.Value - notification.BestBid.Value;

        // 2. Adverse Selection Guard: Never place limit orders into wide phantom spreads
        if (spread > 2d * tickSize) return Task.CompletedTask;

        if (imbalance > 0.8d)
        {
            // 3. True Microstructure Precision: Anchor explicitly to the Order Book BID level
            double frontRunPrice = notification.BestBid.Value + tickSize;
            
            notification.ExecutionAction = ExecutionActionType.LimitFrontRunBuy;
            notification.AddReason(SignalReasonFlags.OFIFrontRunBuy);

            LogBuyWallFrontRun(_logger, imbalance, frontRunPrice);
        }
        else if (imbalance < -0.8d)
        {
            // True Microstructure Precision: Anchor explicitly to the Order Book ASK level
            double frontRunPrice = notification.BestAsk.Value - tickSize;
            
            notification.ExecutionAction = ExecutionActionType.LimitFrontRunSell;
            notification.AddReason(SignalReasonFlags.OFIFrontRunSell);

            LogSellWallFrontRun(_logger, imbalance, frontRunPrice);
        }

        return Task.CompletedTask;
    }

    // Yüksek Performanslı Source Generator Loglama (.NET 6+)
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "[FRONT-RUN] Massive Buy Wall detected ({Imbalance:P2}). Placing defensive Limit Buy at {FrontRunPrice}.")]
    private static partial void LogBuyWallFrontRun(ILogger logger, double imbalance, double frontRunPrice);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "[FRONT-RUN] Massive Sell Wall detected ({Imbalance:P2}). Placing defensive Limit Sell at {FrontRunPrice}.")]
    private static partial void LogSellWallFrontRun(ILogger logger, double imbalance, double frontRunPrice);
}
