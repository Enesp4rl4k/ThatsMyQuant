using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public class PaperTradingService
{
    private readonly ITechnicalAnalysisService _analysis;
    private readonly IMarketDataService _marketData;
    private readonly ILogger<PaperTradingService> _logger;
    private readonly StrategyPromotionService? _promotionService;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;
    private readonly MarketExecutionProfileService _executionProfiles;
    public PaperPortfolio Portfolio { get; } = new();

    public PaperTradingService(
        ITechnicalAnalysisService analysis,
        IMarketDataService marketData,
        ILogger<PaperTradingService> logger,
        StrategyPromotionService? promotionService = null,
        ExitPolicyLearningService? exitPolicyLearningService = null,
        MarketExecutionProfileService? executionProfiles = null)
    {
        _analysis = analysis;
        _marketData = marketData;
        _logger = logger;
        _promotionService = promotionService;
        _exitPolicyLearningService = exitPolicyLearningService;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
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

    private string ResolveMarketType(string symbol)
    {
        return symbol.Contains("-") ? "Crypto" : 
               (symbol.Contains("=") ? "Forex" : "BIST");
    }

    private async Task<string> HandleOpenPosition(PaperPosition position, IReadOnlyList<Candle> candles, MarketExecutionProfile executionProfile)
    {
        var currentPrice = candles.Last().Close;
        position.BarsHeld++;
        position.CurrentPrice = currentPrice;
        ApplyBreakevenProtection(position, candles.Last());
        
        var previousCandle = candles.Count > 1 ? candles[^2] : candles.Last();
        double atr = CalculateAtrProxy(candles, currentPrice);

        bool trailingHit = TrailingStopService.UpdateTrailingStop(position, candles.Last().High, candles.Last().Low, atr);
        string? exitReason = DetermineExitReason(position, candles.Last(), previousCandle, trailingHit);

        if (exitReason != null)
        {
            return await ClosePosition(position, candles.Last(), executionProfile, exitReason);
        }

        return $"{position.Symbol} position open — UnrealizedPnL: ₺{position.UnrealizedPnL:N2}";
    }

    private double CalculateAtrProxy(IReadOnlyList<Candle> candles, decimal currentPrice)
    {
        return candles.Count > 14
            ? (double)(candles.TakeLast(15).Max(c => c.High) - candles.TakeLast(15).Min(c => c.Low)) / 14
            : (double)currentPrice * 0.02;
    }

    private string? DetermineExitReason(PaperPosition position, Candle currentCandle, Candle previousCandle, bool trailingHit)
    {
        if (trailingHit) return "TrailingSL";
        if (position.Side == TradeSide.Buy)
        {
            if (currentCandle.Close <= position.StopLoss) return "StopLoss";
            if (currentCandle.Close >= position.TakeProfit) return "TakeProfit";
        }
        else // Sell
        {
            if (currentCandle.Close >= position.StopLoss) return "StopLoss";
            if (currentCandle.Close <= position.TakeProfit) return "TakeProfit";
        }

        var strategyExit = TryGetStrategyPolicyExit(position, currentCandle, previousCandle);
        if (strategyExit != null) return strategyExit;

        if (position.MaxHoldingBars > 0 && position.BarsHeld >= position.MaxHoldingBars)
        {
            return $"TimeStop({position.ExitPolicyTag})";
        }

        return null;
    }

    private async Task<string> ClosePosition(PaperPosition position, Candle lastCandle, MarketExecutionProfile executionProfile, string exitReason)
    {
        var adjustedExit = _executionProfiles.ApplyExecutionPrice(
            lastCandle.Close,
            position.Side,
            false,
            position.Symbol,
            null,
            executionProfile.SlippagePercent,
            executionProfile.SpreadPercent,
            lastCandle);

        position.ExitTime = DateTime.UtcNow;
        position.ExitReason = exitReason;
        position.RealizedPnL = _executionProfiles.CalculateNetPnl(
            position.EntryPrice,
            adjustedExit,
            position.Quantity,
            position.Side,
            position.Symbol,
            null,
            executionProfile.CommissionPercentPerSide);

        Portfolio.Cash += adjustedExit * position.Quantity + position.RealizedPnL;
        Portfolio.DailyPnL += position.RealizedPnL;
        Portfolio.ClosedPositions.Add(position);
        Portfolio.OpenPositions.Remove(position);
        Portfolio.EquityCurve.Add(Portfolio.TotalEquity);

        await (_promotionService?.RecordPaperState(Portfolio.ClosedPositions) ?? Task.CompletedTask);
        await (_exitPolicyLearningService?.RecordPaperState(Portfolio.ClosedPositions) ?? Task.CompletedTask);

        _logger.LogInformation("📄 PAPER {Exit}: {Symbol} PnL=₺{PnL:N2} ({Reason})",
            exitReason, position.Symbol, position.RealizedPnL, exitReason);

        return $"CLOSED {position.Symbol} via {exitReason} — PnL: ₺{position.RealizedPnL:N2}";
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
            PartialExitFraction = decision.PartialExitFraction <= 0 ? 0.50m : decision.PartialExitFraction,
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

        _logger.LogInformation("📄 PAPER TRADE: {Side} {Qty} {Symbol} @ ₺{Price:F2} | SL=₺{SL:F2} TP=₺{TP:F2}",
            decision.Side, quantity, symbol, currentPrice, decision.StopLoss, decision.TakeProfit);

        return $"OPENED {decision.Side} {quantity} {symbol} @ ₺{currentPrice:F2} (Score: {decision.Confidence}/100)";
    }
    private static void ApplyBreakevenProtection(PaperPosition position, Candle candle)
    {
        if (position.IsBreakevenArmed || position.BreakevenTriggerFraction <= 0)
        {
            return;
        }

        var targetDistance = Math.Abs(position.TakeProfit - position.EntryPrice);
        if (targetDistance <= 0)
        {
            return;
        }

        var triggerDistance = targetDistance * position.BreakevenTriggerFraction;
        var triggerReached = position.Side == TradeSide.Buy
            ? candle.High >= position.EntryPrice + triggerDistance
            : candle.Low <= position.EntryPrice - triggerDistance;

        if (!triggerReached)
        {
            return;
        }

        position.IsBreakevenArmed = true;
        var buffer = position.EntryPrice * 0.0005m;

        if (position.Side == TradeSide.Buy)
        {
            position.StopLoss = Math.Max(position.StopLoss, position.EntryPrice + buffer);
        }
        else
        {
            position.StopLoss = position.StopLoss == 0
                ? position.EntryPrice - buffer
                : Math.Min(position.StopLoss, position.EntryPrice - buffer);
        }
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

    private static string? TryGetStrategyPolicyExit(PaperPosition position, Candle candle, Candle previousCandle)
    {
        var bearishBar = candle.Close < candle.Open && candle.Close < previousCandle.Close;
        var bullishBar = candle.Close > candle.Open && candle.Close > previousCandle.Close;

        return position.ExitPolicyTag switch
        {
            "TrendRunner" when position.IsBreakevenArmed &&
                                position.BarsHeld >= Math.Max(6, position.MaxHoldingBars / 2) &&
                                ((position.Side == TradeSide.Buy && bearishBar) || (position.Side == TradeSide.Sell && bullishBar))
                => "TrendMomentumFade",
            "MeanReversion" when position.BarsHeld >= 4 && !position.IsBreakevenArmed
                => "MeanReversionTimeout",
            "BreakoutExpansion" when position.BarsHeld >= 3 && position.IsBreakevenArmed &&
                                       ((position.Side == TradeSide.Buy && bearishBar) || (position.Side == TradeSide.Sell && bullishBar))
                => "BreakoutFailure",
            "HftScalp" when position.BarsHeld >= 2
                => "HftMicroExit",
            _ => null
        };
    }
}
