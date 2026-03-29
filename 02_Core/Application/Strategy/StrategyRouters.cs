using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy;

public interface IStrategyRouter
{
    bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime);
    int EntryThreshold { get; }
    string StrategyTag { get; }
    TradeDecision BuildDecision(StrategyRouterContext context);
}

public sealed record StrategyRouterContext(
    Signal Signal,
    IndicatorSnapshot Indicators,
    MarketRegimeDetector.RegimeResult Regime,
    TradeSide Side,
    int Confidence,
    string SessionReason,
    string TradeabilityReason,
    decimal BasePositionSize,
    decimal CurrentPrice,
    decimal Atr,
    List<string> Reasons);

public sealed class TrendStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 58;
    public string StrategyTag => "TrendConfluence";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => regime.Regime == MarketRegime.Trending && !StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * 1.25m)
            : context.CurrentPrice + (context.Atr * 1.25m);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * 3.20m)
            : context.CurrentPrice - (context.Atr * 3.20m);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.45m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.45m);

        return StrategyRouterFactory.BuildTradeDecision(context, StrategyTag, stopLoss, takeProfit, partialTakeProfit, 0.40m, 0.55m, 18);
    }
}

public sealed class RangeStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 63;
    public string StrategyTag => "RangeReversion";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => regime.Regime == MarketRegime.Ranging && !StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * 0.95m)
            : context.CurrentPrice + (context.Atr * 0.95m);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * 1.60m)
            : context.CurrentPrice - (context.Atr * 1.60m);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.60m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.60m);

        return StrategyRouterFactory.BuildTradeDecision(context, StrategyTag, stopLoss, takeProfit, partialTakeProfit, 0.55m, 0.35m, 8);
    }
}

public sealed class BreakoutStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 68;
    public string StrategyTag => "BreakoutHunter";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => regime.Regime == MarketRegime.Volatile && !StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var volumeStretch = (context.Indicators.VolumeRatio ?? 1.0) >= 2.0 ? 4.0m : 3.4m;
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * 1.45m)
            : context.CurrentPrice + (context.Atr * 1.45m);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * volumeStretch)
            : context.CurrentPrice - (context.Atr * volumeStretch);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.35m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.35m);

        return StrategyRouterFactory.BuildTradeDecision(context, StrategyTag, stopLoss, takeProfit, partialTakeProfit, 0.33m, 0.25m, 12);
    }

    private static bool IsHftSignal(Signal signal)
        => signal.Source.Contains("HFT", StringComparison.OrdinalIgnoreCase) ||
           signal.Metadata.TryGetValue("ExecutionMode", out var mode) && mode.Contains("HFT", StringComparison.OrdinalIgnoreCase);
}

public sealed class HftStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 72;
    public string StrategyTag => "HftMicroEdge";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var volumeRatio = (decimal)(context.Indicators.VolumeRatio ?? 1.0);
        var stopAtr = volumeRatio >= 1.8m ? 0.55m : 0.45m;
        var tpAtr = volumeRatio >= 1.8m ? 0.95m : 0.75m;
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * stopAtr)
            : context.CurrentPrice + (context.Atr * stopAtr);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * tpAtr)
            : context.CurrentPrice - (context.Atr * tpAtr);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.30m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.30m);

        return StrategyRouterFactory.BuildTradeDecision(context, StrategyTag, stopLoss, takeProfit, partialTakeProfit, 0.25m, 0.20m, 4);
    }
}

internal static class StrategyRouterFactory
{
    internal static bool IsHftSignal(Signal signal)
        => signal.Source.Contains("HFT", StringComparison.OrdinalIgnoreCase) ||
           (signal.Metadata.TryGetValue("ExecutionMode", out var mode) && mode.Contains("HFT", StringComparison.OrdinalIgnoreCase));

    internal static TradeDecision BuildTradeDecision(
        StrategyRouterContext context,
        string strategyTag,
        decimal stopLoss,
        decimal takeProfit,
        decimal partialTakeProfit,
        decimal partialExitFraction,
        decimal breakevenTriggerFraction,
        int maxHoldingBars)
    {
        var reasoning = $"Confluence {context.Confidence}/100 ({context.Side}, {context.Regime.Regime}). {string.Join(" | ", context.Reasons)}. " +
                        $"{context.SessionReason}. {context.TradeabilityReason}. " +
                        $"SL: {stopLoss:F2}, TP: {takeProfit:F2}, Partial: {partialTakeProfit:F2} ({partialExitFraction:P0}), " +
                        $"BE: {breakevenTriggerFraction:P0}, TimeStop: {maxHoldingBars} bars, Size: {context.BasePositionSize:P1}.";

        return new TradeDecision
        {
            ShouldTrade = true,
            Confidence = context.Confidence,
            Side = context.Side,
            Reasoning = reasoning,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            PositionSizePercent = context.BasePositionSize,
            StrategyTag = strategyTag,
            RegimeTag = context.Regime.Regime.ToString(),
            PartialTakeProfit = partialTakeProfit,
            PartialExitFraction = partialExitFraction,
            ExitPolicyTag = strategyTag switch
            {
                "RangeReversion" => "MeanReversion",
                "BreakoutHunter" => "BreakoutExpansion",
                "HftMicroEdge" => "HftScalp",
                _ => "TrendRunner"
            },
            BreakevenTriggerFraction = breakevenTriggerFraction,
            MaxHoldingBars = maxHoldingBars,
            Indicators = context.Indicators
        };
    }
}
