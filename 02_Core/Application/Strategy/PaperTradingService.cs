using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public class PaperTradingService
{
    private readonly ITechnicalAnalysisService _analysis;
    private readonly IMarketDataService _marketData;
    private readonly ILogger<PaperTradingService> _logger;
    private readonly StrategyPromotionService? _promotionService;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly PositionSupervisorService _positionSupervisor;
    public PaperPortfolio Portfolio { get; } = new();

    public PaperTradingService(
        ITechnicalAnalysisService analysis,
        IMarketDataService marketData,
        ILogger<PaperTradingService> logger,
        StrategyPromotionService? promotionService = null,
        ExitPolicyLearningService? exitPolicyLearningService = null,
        MarketExecutionProfileService? executionProfiles = null,
        PositionSupervisorService? positionSupervisor = null)
    {
        _analysis = analysis;
        _marketData = marketData;
        _logger = logger;
        _promotionService = promotionService;
        _exitPolicyLearningService = exitPolicyLearningService;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _positionSupervisor = positionSupervisor ?? new PositionSupervisorService();
    }

    public async Task<string> ProcessSymbolAsync(string symbol)
    {
        string market = ResolveMarketType(symbol);
        var executionProfile = _executionProfiles.ResolveForSymbol(symbol);

        var session = MarketHoursScheduler.GetCurrentSession(market);
        if (!session.CanTrade)
        {
            return $"{market} {session.Description}. Next: {session.NextSessionStart:HH:mm}";
        }

        Portfolio.ResetDailyPnLIfNeeded();
        var candles = await _marketData.GetCandlesAsync(symbol, 100, market);
        var currentPrice = candles.Last().Close;

        var position = Portfolio.OpenPositions.FirstOrDefault(p => p.Symbol == symbol);
        if (position != null)
        {
            return await HandleOpenPosition(position, candles, executionProfile);
        }

        if (!IsPreferredTradeWindow(market, candles.Last().Timestamp))
        {
            return $"Session filter: {market} is outside preferred trade window for new entries.";
        }

        var constraintMessage = CheckTradingConstraints();
        if (constraintMessage != null) return constraintMessage;

        var openSymbols = Portfolio.OpenPositions.Select(p => p.Symbol).ToList();
        var corrCheck = CorrelationService.CheckCorrelation(symbol, openSymbols);
        if (corrCheck.ExceedsSectorLimit)
        {
            _logger.LogWarning("Correlation block: {Rec}", corrCheck.Recommendation);
            return corrCheck.Recommendation;
        }

        return await OpenNewPosition(symbol, market, currentPrice, candles, executionProfile);
    }

    private static string ResolveMarketType(string symbol)
    {
        return symbol.Contains("-", StringComparison.OrdinalIgnoreCase)
            ? "Crypto"
            : (symbol.Contains("=", StringComparison.OrdinalIgnoreCase) ? "Forex" : "BIST");
    }

    private async Task<string> HandleOpenPosition(PaperPosition position, IReadOnlyList<Candle> candles, MarketExecutionProfile executionProfile)
    {
        var currentPrice = candles.Last().Close;
        var previousCandle = candles.Count > 1 ? candles[^2] : candles.Last();
        var atr = (decimal)CalculateAtrProxy(candles, currentPrice);
        var lifecycleAction = _positionSupervisor.AdvancePaperPosition(position, candles.Last(), previousCandle, atr);

        if (lifecycleAction?.ActionType == PositionLifecycleActionType.PartialExit)
        {
            return await ApplyPartialExit(position, candles.Last(), executionProfile, lifecycleAction);
        }

        if (lifecycleAction?.ActionType == PositionLifecycleActionType.FullExit)
        {
            return await ClosePosition(position, candles.Last(), executionProfile, lifecycleAction.Reason);
        }

        return $"{position.Symbol} position open — UnrealizedPnL: ₺{position.UnrealizedPnL:N2}";
    }

    private double CalculateAtrProxy(IReadOnlyList<Candle> candles, decimal currentPrice)
    {
        return candles.Count > 14
            ? (double)(candles.TakeLast(15).Max(c => c.High) - candles.TakeLast(15).Min(c => c.Low)) / 14
            : (double)currentPrice * 0.02;
    }

    private async Task<string> ClosePosition(PaperPosition position, Candle lastCandle, MarketExecutionProfile executionProfile, string exitReason)
    {
        var exitQuantity = Math.Max(0m, position.Quantity);
        var adjustedExit = _executionProfiles.ApplyExecutionPrice(
            lastCandle.Close,
            position.Side,
            false,
            position.Symbol,
            null,
            executionProfile.SlippagePercent,
            executionProfile.SpreadPercent,
            lastCandle);

        var exitPnl = _executionProfiles.CalculateNetPnl(
            position.EntryPrice,
            adjustedExit,
            exitQuantity,
            position.Side,
            position.Symbol,
            null,
            executionProfile.CommissionPercentPerSide);

        position.ExitTime = DateTime.UtcNow;
        position.ExitReason = exitReason;
        position.RealizedPnL += exitPnl;
        position.Quantity = 0m;
        position.RemainingQuantity = 0m;

        Portfolio.Cash += (position.EntryPrice * exitQuantity) + exitPnl;
        Portfolio.DailyPnL += exitPnl;
        Portfolio.ClosedPositions.Add(position);
        Portfolio.OpenPositions.Remove(position);
        Portfolio.EquityCurve.Add(Portfolio.TotalEquity);

        await (_promotionService?.RecordPaperState(Portfolio.ClosedPositions) ?? Task.CompletedTask);
        await (_exitPolicyLearningService?.RecordPaperState(Portfolio.ClosedPositions) ?? Task.CompletedTask);

        _logger.LogInformation("PAPER {Exit}: {Symbol} PnL=₺{PnL:N2} ({Reason})",
            exitReason, position.Symbol, position.RealizedPnL, exitReason);

        return $"CLOSED {position.Symbol} via {exitReason} — PnL: ₺{position.RealizedPnL:N2}";
    }

    private async Task<string> ApplyPartialExit(PaperPosition position, Candle lastCandle, MarketExecutionProfile executionProfile, PositionLifecycleAction action)
    {
        var exitQuantity = Math.Min(Math.Max(0m, position.Quantity), action.Quantity);
        if (exitQuantity <= 0m)
        {
            return $"{position.Symbol} position open — UnrealizedPnL: ₺{position.UnrealizedPnL:N2}";
        }

        var adjustedExit = _executionProfiles.ApplyExecutionPrice(
            lastCandle.Close,
            position.Side,
            false,
            position.Symbol,
            null,
            executionProfile.SlippagePercent,
            executionProfile.SpreadPercent,
            lastCandle);

        var partialPnl = _executionProfiles.CalculateNetPnl(
            position.EntryPrice,
            adjustedExit,
            exitQuantity,
            position.Side,
            position.Symbol,
            null,
            executionProfile.CommissionPercentPerSide);

        position.HasTakenPartialProfit = true;
        position.RealizedPnL += partialPnl;
        position.Quantity = Math.Max(0m, position.Quantity - exitQuantity);
        position.RemainingQuantity = position.Quantity;
        position.CurrentPrice = lastCandle.Close;
        position.ExitReason = action.Reason;

        Portfolio.Cash += (position.EntryPrice * exitQuantity) + partialPnl;
        Portfolio.DailyPnL += partialPnl;
        Portfolio.EquityCurve.Add(Portfolio.TotalEquity);

        _logger.LogInformation("PAPER PARTIAL EXIT: {Symbol} Qty={Qty} PnL=₺{PnL:N2} ({Reason})",
            position.Symbol, exitQuantity, partialPnl, action.Reason);

        if (position.Quantity <= 0m)
        {
            return await ClosePosition(position, lastCandle, executionProfile, action.Reason);
        }

        return $"PARTIAL {position.Symbol} via {action.Reason} — booked ₺{partialPnl:N2}, remaining {position.Quantity:N4}";
    }

    private string? CheckTradingConstraints()
    {
        if (Portfolio.DailyPnL < -(Portfolio.InitialBalance * 0.02m))
        {
            return $"⚠️ Daily loss limit hit (₺{Portfolio.DailyPnL:N2}). No new trades today.";
        }

        var consecutiveLosses = Portfolio.ClosedPositions
            .OrderByDescending(p => p.ExitTime)
            .Take(4)
            .TakeWhile(p => p.RealizedPnL < 0)
            .Count();

        if (consecutiveLosses >= 3)
        {
            return $"Cooldown active after {consecutiveLosses} consecutive paper losses. Wait for a cleaner window.";
        }

        return null;
    }

    private async Task<string> OpenNewPosition(string symbol, string market, decimal currentPrice, IReadOnlyList<Candle> candles, MarketExecutionProfile executionProfile)
    {
        var signal = new Signal { Symbol = symbol, Price = currentPrice };
        var risk = RiskParameters.Default;
        var decision = await _analysis.AnalyzeAsync(signal, candles.ToList(), risk);

        if (!decision.ShouldTrade)
            return $"SKIP {symbol}: {decision.Reasoning}";

        decimal adaptiveSize = AdaptiveSizingService.Apply(decision.PositionSizePercent, decision.Confidence, market, DateTime.UtcNow);
        decimal positionValue = Portfolio.Cash * adaptiveSize;
        decimal quantity = currentPrice > 0 ? Math.Floor(positionValue / currentPrice) : 0;

        if (quantity <= 0) return $"SKIP {symbol}: insufficient cash for position";

        var newPosition = new PaperPosition
        {
            Symbol = symbol,
            Side = decision.Side,
            EntryPrice = _executionProfiles.ApplyExecutionPrice(
                currentPrice,
                decision.Side,
                true,
                symbol,
                null,
                executionProfile.SlippagePercent,
                executionProfile.SpreadPercent,
                candles.Last()),
            CurrentPrice = currentPrice,
            Quantity = quantity,
            StopLoss = decision.StopLoss,
            TakeProfit = decision.TakeProfit,
            PartialTakeProfit = decision.PartialTakeProfit,
            PartialExitFraction = decision.PartialExitFraction <= 0m ? 0.50m : decision.PartialExitFraction,
            RemainingQuantity = quantity,
            ExitPolicyTag = decision.ExitPolicyTag,
            BreakevenTriggerFraction = decision.BreakevenTriggerFraction,
            MaxHoldingBars = decision.MaxHoldingBars,
            ExecutionMode = "PAPER",
            PromotionStage = "Paper",
            OptimalPriceSinceEntry = currentPrice,
            EntryTime = DateTime.UtcNow
        };

        Portfolio.Cash -= quantity * newPosition.EntryPrice;
        Portfolio.OpenPositions.Add(newPosition);
        Portfolio.EquityCurve.Add(Portfolio.TotalEquity);

        _logger.LogInformation("PAPER TRADE: {Side} {Qty} {Symbol} @ ₺{Price:F2} | SL=₺{SL:F2} TP=₺{TP:F2}",
            decision.Side, quantity, symbol, currentPrice, decision.StopLoss, decision.TakeProfit);

        return $"OPENED {decision.Side} {quantity} {symbol} @ ₺{currentPrice:F2} (Score: {decision.Confidence}/100)";
    }

    private static bool IsPreferredTradeWindow(string market, DateTime timestamp)
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
}
