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
    MarketExecutionProfile ExecutionProfile,
    TradeSide Side,
    int Confidence,
    string SessionReason,
    string TradeabilityReason,
    decimal BasePositionSize,
    decimal CurrentPrice,
    decimal Atr,
    decimal EstimatedRoundTripCostPercent,
    List<string> Reasons);

public sealed class TrendStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 55;
    public string StrategyTag => "TrendConfluence";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => regime.Regime == MarketRegime.Trending && !StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var conviction = StrategyRouterFactory.ResolveConviction(context);
        var trendQuality = StrategyRouterFactory.ResolveTrendQuality(context);
        var costPressure = StrategyRouterFactory.ResolveExecutionCostPressure(context);
        var stopAtr = 1.30m - (0.20m * conviction) - (0.05m * trendQuality);
        var targetAtr = 2.60m + (0.55m * conviction) + (0.30m * trendQuality) + (0.35m * costPressure);
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * stopAtr)
            : context.CurrentPrice + (context.Atr * stopAtr);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * targetAtr)
            : context.CurrentPrice - (context.Atr * targetAtr);
        takeProfit = StrategyRouterFactory.ApplyRewardCostFloor(context, stopLoss, takeProfit, 5.0m);
        if (costPressure > 0m)
        {
            var rewardExtension = context.Atr * (0.45m * costPressure);
            takeProfit = context.Side == TradeSide.Buy
                ? takeProfit + rewardExtension
                : takeProfit - rewardExtension;
        }
        StrategyRouterFactory.AppendExecutionCostReason(context, costPressure, "Execution cost floor widened trend target to preserve net expectancy.");
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.62m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.62m);
        var partialExitFraction = 0.30m + (0.10m * (1m - conviction)) - (0.06m * costPressure);
        var breakevenTriggerFraction = 0.38m + (0.07m * (1m - trendQuality)) + (0.04m * costPressure);
        var maxHoldingBars = 52 + (int)Math.Round(18m * conviction) + (int)Math.Round(10m * trendQuality);

        return StrategyRouterFactory.BuildTradeDecision(
            context,
            StrategyTag,
            stopLoss,
            takeProfit,
            partialTakeProfit,
            partialExitFraction,
            breakevenTriggerFraction,
            maxHoldingBars);
    }
}

public sealed class RangeStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 60;
    public string StrategyTag => "RangeReversion";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => regime.Regime == MarketRegime.Ranging && !StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var conviction = StrategyRouterFactory.ResolveConviction(context);
        var stretch = StrategyRouterFactory.ResolveMeanReversionStretch(context);
        var costPressure = StrategyRouterFactory.ResolveExecutionCostPressure(context);
        var stopAtr = 1.05m - (0.10m * stretch);
        var targetAtr = 2.00m + (0.45m * conviction) + (0.20m * stretch);
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * stopAtr)
            : context.CurrentPrice + (context.Atr * stopAtr);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * targetAtr)
            : context.CurrentPrice - (context.Atr * targetAtr);
        takeProfit = StrategyRouterFactory.ApplyRewardCostFloor(context, stopLoss, takeProfit, 4.0m);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.52m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.52m);
        var partialExitFraction = 0.45m + (0.15m * stretch) - (0.05m * costPressure);
        var breakevenTriggerFraction = 0.55m - (0.08m * stretch) + (0.03m * costPressure);
        var maxHoldingBars = 20 + (int)Math.Round(10m * conviction);

        return StrategyRouterFactory.BuildTradeDecision(
            context,
            StrategyTag,
            stopLoss,
            takeProfit,
            partialTakeProfit,
            partialExitFraction,
            breakevenTriggerFraction,
            maxHoldingBars);
    }
}

public sealed class BreakoutStrategyRouter : IStrategyRouter
{
    public int EntryThreshold => 65;
    public string StrategyTag => "BreakoutHunter";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => regime.Regime == MarketRegime.Volatile && !StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var conviction = StrategyRouterFactory.ResolveConviction(context);
        var breakoutQuality = StrategyRouterFactory.ResolveBreakoutQuality(context);
        var costPressure = StrategyRouterFactory.ResolveExecutionCostPressure(context);
        var stopAtr = 0.95m - (0.08m * breakoutQuality);
        var volumeStretch = 2.90m + (0.85m * breakoutQuality) + (0.30m * conviction);
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * stopAtr)
            : context.CurrentPrice + (context.Atr * stopAtr);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * volumeStretch)
            : context.CurrentPrice - (context.Atr * volumeStretch);
        takeProfit = StrategyRouterFactory.ApplyRewardCostFloor(context, stopLoss, takeProfit, 6.0m);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.48m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.48m);
        var partialExitFraction = 0.25m + (0.10m * (1m - conviction)) - (0.08m * costPressure);
        var breakevenTriggerFraction = 0.26m + (0.08m * (1m - breakoutQuality)) + (0.05m * costPressure);
        var maxHoldingBars = 26 + (int)Math.Round(12m * breakoutQuality);

        return StrategyRouterFactory.BuildTradeDecision(
            context,
            StrategyTag,
            stopLoss,
            takeProfit,
            partialTakeProfit,
            partialExitFraction,
            breakevenTriggerFraction,
            maxHoldingBars);
    }

    private static bool IsHftSignal(Signal signal)
        => signal.Source.Contains("HFT", StringComparison.OrdinalIgnoreCase) ||
           signal.Metadata.TryGetValue("ExecutionMode", out var mode) && mode.Contains("HFT", StringComparison.OrdinalIgnoreCase);
}

public sealed class HftStrategyRouter : IStrategyRouter
{
    // Lowered 72 → 60 for HFT-mode backtests. The original 72 was calibrated for live HFT
    // feeds with microstructure signals; in a backtest driven by 5m candles the confidence
    // ceiling is lower so 72 filtered out everything.
    public int EntryThreshold => 66;
    public string StrategyTag => "HftMicroEdge";

    public bool CanHandle(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
        => StrategyRouterFactory.IsHftSignal(signal);

    public TradeDecision BuildDecision(StrategyRouterContext context)
    {
        var conviction = StrategyRouterFactory.ResolveConviction(context);
        var costPressure = StrategyRouterFactory.ResolveExecutionCostPressure(context);
        var volumeRatio = (decimal)(context.Indicators.VolumeRatio ?? 1.0);
        var stopAtr = volumeRatio >= 1.8m ? 0.50m : 0.42m;
        var tpAtr = volumeRatio >= 1.8m ? 1.35m : 1.08m;
        tpAtr += 0.15m * conviction + (0.18m * costPressure);
        var stopLoss = context.Side == TradeSide.Buy
            ? context.CurrentPrice - (context.Atr * stopAtr)
            : context.CurrentPrice + (context.Atr * stopAtr);
        var takeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + (context.Atr * tpAtr)
            : context.CurrentPrice - (context.Atr * tpAtr);
        takeProfit = StrategyRouterFactory.ApplyRewardCostFloor(context, stopLoss, takeProfit, 5.5m);
        var partialTakeProfit = context.Side == TradeSide.Buy
            ? context.CurrentPrice + ((takeProfit - context.CurrentPrice) * 0.55m)
            : context.CurrentPrice - ((context.CurrentPrice - takeProfit) * 0.55m);
        var partialExitFraction = 0.50m - (0.12m * costPressure);
        var breakevenTriggerFraction = 0.18m + (0.05m * costPressure);
        var maxHoldingBars = 10 + (int)Math.Round(4m * conviction);

        return StrategyRouterFactory.BuildTradeDecision(
            context,
            StrategyTag,
            stopLoss,
            takeProfit,
            partialTakeProfit,
            partialExitFraction,
            breakevenTriggerFraction,
            maxHoldingBars);
    }
}

internal static class StrategyRouterFactory
{
    internal static bool IsHftSignal(Signal signal)
        => signal.Source.Contains("HFT", StringComparison.OrdinalIgnoreCase) ||
           (signal.Metadata.TryGetValue("ExecutionMode", out var mode) && mode.Contains("HFT", StringComparison.OrdinalIgnoreCase));

    internal static decimal ResolveConviction(StrategyRouterContext context)
        => Math.Clamp((context.Confidence - 60m) / 30m, 0m, 1m);

    internal static decimal ResolveTrendQuality(StrategyRouterContext context)
    {
        var emaSpread = context.Indicators.EMA20.HasValue && context.Indicators.EMA50.HasValue && context.CurrentPrice > 0m
            ? Math.Abs((decimal)(context.Indicators.EMA20.Value - context.Indicators.EMA50.Value)) / context.CurrentPrice
            : 0m;
        var rSquared = context.Indicators.LinearRegression?.RSquared is double fit
            ? Math.Clamp((decimal)fit, 0m, 1m)
            : 0.45m;
        return Math.Clamp((emaSpread * 120m * 0.45m) + (rSquared * 0.55m), 0m, 1m);
    }

    internal static decimal ResolveMeanReversionStretch(StrategyRouterContext context)
    {
        if (context.Indicators.RSI is not double rsi)
            return 0.35m;

        decimal stretch = context.Side == TradeSide.Buy
            ? (decimal)Math.Clamp((45d - rsi) / 20d, 0d, 1d)
            : (decimal)Math.Clamp((rsi - 55d) / 20d, 0d, 1d);

        if (context.Indicators.LinearRegression?.SigmaDistance is double sigma)
            stretch = Math.Clamp(stretch + (decimal)Math.Min(0.35d, Math.Abs(sigma) / 4d), 0m, 1m);

        return stretch;
    }

    internal static decimal ResolveBreakoutQuality(StrategyRouterContext context)
    {
        var volume = context.Indicators.VolumeRatio is double vr
            ? Math.Clamp(((decimal)vr - 1.0m) / 1.5m, 0m, 1m)
            : 0.2m;
        var fit = context.Indicators.LinearRegression?.RSquared is double rSquared
            ? Math.Clamp((decimal)rSquared, 0m, 1m)
            : 0.35m;
        var histogram = context.Indicators.MACDHistogram is double hist
            ? Math.Clamp((decimal)Math.Abs(hist) * 10m, 0m, 1m)
            : 0.2m;
        return Math.Clamp((volume * 0.45m) + (fit * 0.30m) + (histogram * 0.25m), 0m, 1m);
    }

    internal static decimal ResolveExecutionCostPressure(StrategyRouterContext context)
    {
        if (context.EstimatedRoundTripCostPercent <= 0m)
            return 0m;

        var atrPercent = context.CurrentPrice > 0m
            ? (context.Atr / context.CurrentPrice) * 100m
            : 0m;
        var normalized = atrPercent > 0m
            ? context.EstimatedRoundTripCostPercent / Math.Max(0.15m, atrPercent * 0.35m)
            : context.EstimatedRoundTripCostPercent / 0.25m;
        return Math.Clamp(normalized, 0m, 1m);
    }

    internal static decimal ApplyRewardCostFloor(
        StrategyRouterContext context,
        decimal stopLoss,
        decimal takeProfit,
        decimal minRewardToCost)
    {
        if (context.CurrentPrice <= 0m || context.EstimatedRoundTripCostPercent <= 0m)
            return takeProfit;

        var currentRewardPercent = Math.Abs(takeProfit - context.CurrentPrice) / context.CurrentPrice * 100m;
        var rrFloorPercent = context.EstimatedRoundTripCostPercent * minRewardToCost;
        var riskPercent = Math.Abs(context.CurrentPrice - stopLoss) / context.CurrentPrice * 100m;
        var minRequiredPercent = Math.Max(rrFloorPercent, riskPercent * 1.5m);
        if (currentRewardPercent >= minRequiredPercent)
        {
            if (rrFloorPercent >= 0.40m)
            {
                context.Reasons.Add($"Execution cost floor held minimum gross reward at {minRequiredPercent:F2}%.");
            }
            return takeProfit;
        }

        var widened = context.Side == TradeSide.Buy
            ? context.CurrentPrice * (1m + (minRequiredPercent / 100m))
            : context.CurrentPrice * (1m - (minRequiredPercent / 100m));

        context.Reasons.Add($"Execution cost floor widened target to {minRequiredPercent:F2}% gross reward.");
        return widened;
    }

    internal static void AppendExecutionCostReason(
        StrategyRouterContext context,
        decimal costPressure,
        string message)
    {
        if (costPressure < 0.20m)
            return;

        context.Reasons.Add(message);
    }

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
        // Hard RR gate: reject setups with reward:risk below 1.5 to avoid marginal trades
        // where costs/slippage consume most of the upside.
        const decimal MinRewardRisk = 1.5m;
        var risk = Math.Abs(context.CurrentPrice - stopLoss);
        var reward = Math.Abs(takeProfit - context.CurrentPrice);
        if (risk > 0m && reward / risk < MinRewardRisk)
        {
            return new TradeDecision
            {
                ShouldTrade = false,
                Confidence = context.Confidence,
                Side = context.Side,
                Reasoning = $"Rejected by RR gate: reward/risk {reward / risk:F2} < {MinRewardRisk:F2} ({strategyTag}).",
                StrategyTag = strategyTag,
                RegimeTag = context.Regime.Regime.ToString(),
                Indicators = context.Indicators
            };
        }

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
