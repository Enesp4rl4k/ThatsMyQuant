using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using System.Collections.Concurrent;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;

namespace ThatsMyQuant.Application.Strategy;

public sealed class HftSignalGenerationService : IHftSignalGenerationService
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly ILogger<HftSignalGenerationService> _logger;
    private readonly HftStrategyRouter _router = new();
    private readonly ConcurrentDictionary<string, (DateTime LastCandleTime, IndicatorSnapshot Snapshot)> _indicatorStateCache = new();
    private static readonly ConcurrentDictionary<string, string> _marketTypeCache = new();

    public HftSignalGenerationService(
        ITechnicalAnalysisService technicalAnalysis,
        ILogger<HftSignalGenerationService>? logger = null)
    {
        _technicalAnalysis = technicalAnalysis;
        _logger = logger ?? NullLogger<HftSignalGenerationService>.Instance;
    }

    public async Task<TradeDecision> AnalyzeAsync(Signal signal, List<Candle> candles, RiskParameters risk)
    {
        if (candles.Count < 35)
        {
            return TradeDecision.Reject("HFT engine needs at least 35 candles for microstructure routing.");
        }

        var lastCandle = candles.Last();
        
        // Intra-candle streaming optimization: Only compute full indicators if the candle timestamp rolled over
        if (!_indicatorStateCache.TryGetValue(signal.Symbol, out var cachedState) || cachedState.LastCandleTime != lastCandle.Timestamp)
        {
            var newIndicators = await _technicalAnalysis.ComputeIndicatorsAsync(candles);
            cachedState = (lastCandle.Timestamp, newIndicators);
            _indicatorStateCache[signal.Symbol] = cachedState;
        }

        // Stream the live tick price into the frozen snapshot via non-destructive mutation
        var indicators = cachedState.Snapshot with { CurrentPrice = signal.Price };

        if (indicators.RSI is null || indicators.MACDHistogram is null || indicators.ATR is null)
        {
            return TradeDecision.Reject("HFT engine missing indicator state.", indicators);
        }

        var regime = MarketRegimeDetector.Detect(candles);
        var market = ResolveMarket(signal.Symbol);
        var sessionGate = EvaluateSessionGate(market, lastCandle.Timestamp);
        if (!sessionGate.AllowTrade)
        {
            return TradeDecision.Reject($"HFT session gate blocked trade: {sessionGate.Reason}", indicators);
        }

        if (signal.Volume24h > 0 && signal.Volume24h < 600_000m)
        {
            return TradeDecision.Reject($"HFT liquidity gate failed: volume {signal.Volume24h:N0} is too weak.", indicators);
        }

        var momentumScore = 0;
        var reasons = new List<string>();
        var buyMomentum = (indicators.MACDHistogram ?? 0) > 0 && (indicators.EMA20 ?? 0) >= (indicators.EMA50 ?? 0);
        var sellMomentum = (indicators.MACDHistogram ?? 0) < 0 && (indicators.EMA20 ?? 0) <= (indicators.EMA50 ?? 0);
        var side = buyMomentum ? TradeSide.Buy : TradeSide.Sell;

        if (buyMomentum)
        {
            momentumScore += 28;
            reasons.Add("Micro momentum aligned long.");
        }
        else if (sellMomentum)
        {
            momentumScore += 28;
            reasons.Add("Micro momentum aligned short.");
        }
        else
        {
            reasons.Add("Momentum stack is not aligned for HFT.");
        }

        var volumeRatio = indicators.VolumeRatio ?? 1.0;
        if (volumeRatio >= 1.4)
        {
            momentumScore += 18;
            reasons.Add($"Volume ratio {volumeRatio:F2}x supports micro entry.");
        }
        else
        {
            reasons.Add($"Volume ratio {volumeRatio:F2}x is soft for HFT.");
        }

        var atrPercent = indicators.CurrentPrice > 0 && indicators.ATR.HasValue
            ? Convert.ToDecimal(indicators.ATR.Value) / indicators.CurrentPrice * 100m
            : 0m;
        if (atrPercent is >= 0.08m and <= 1.20m)
        {
            momentumScore += 14;
            reasons.Add($"ATR {atrPercent:F2}% is within scalp band.");
        }
        else
        {
            reasons.Add($"ATR {atrPercent:F2}% is outside preferred HFT band.");
        }

        if (indicators.PMax?.IsBullish == true && side == TradeSide.Buy ||
            indicators.PMax?.IsBullish == false && side == TradeSide.Sell)
        {
            momentumScore += 12;
            reasons.Add("PMax confirms micro bias.");
        }

        if (indicators.RSI is >= 35 and <= 65)
        {
            momentumScore += 10;
            reasons.Add($"RSI {indicators.RSI:F1} is balanced for continuation.");
        }

        if (regime.Regime == MarketRegime.Volatile)
        {
            momentumScore += 8;
            reasons.Add("Volatility regime supports HFT expansion.");
        }
        else if (regime.Regime == MarketRegime.Ranging)
        {
            momentumScore -= 6;
            reasons.Add("Range regime penalizes HFT follow-through.");
        }

        momentumScore = Math.Max(0, (int)Math.Round(momentumScore * (double)sessionGate.ScoreMultiplier));

        if (momentumScore < _router.EntryThreshold)
        {
            return TradeDecision.Reject(
                $"HFT entry score too weak ({momentumScore}/100, needs {_router.EntryThreshold}). {string.Join(" | ", reasons)}",
                indicators);
        }

        var atr = indicators.ATR.HasValue
            ? Convert.ToDecimal(indicators.ATR.Value)
            : indicators.CurrentPrice * 0.005m;
        var baseSize = Math.Clamp((risk.MaxPositionSizePercent / 100m) * 0.35m * sessionGate.SizeMultiplier, 0.0025m, 0.02m);

        var decision = _router.BuildDecision(new StrategyRouterContext(
            signal,
            indicators,
            regime,
            side,
            momentumScore,
            sessionGate.Reason,
            "Dedicated HFT signal generation path",
            baseSize,
            indicators.CurrentPrice,
            atr,
            reasons));

        _logger.LogInformation(
            "HFT signal generated for {Symbol}: confidence={Confidence}, side={Side}, regime={Regime}",
            signal.Symbol,
            decision.Confidence,
            decision.Side,
            regime.Regime);

        return decision;
    }

    private static string ResolveMarket(string symbol)
    {
        if (_marketTypeCache.TryGetValue(symbol, out var market))
        {
            return market;
        }

        if (symbol.Contains("-", StringComparison.OrdinalIgnoreCase))
        {
            market = "Crypto";
        }
        else if (symbol.Contains("=", StringComparison.OrdinalIgnoreCase))
        {
            market = "Forex";
        }
        else
        {
            market = "BIST";
        }

        _marketTypeCache[symbol] = market;
        return market;
    }

    private static SessionGate EvaluateSessionGate(string market, DateTime timestamp)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionGate(true, 1.00m, 1.00m, "Primary BIST liquidity window");
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");
        var hour = turkeyTime.Hour;

        if (hour is >= 10 and < 13)
        {
            return new SessionGate(true, 1.00m, 1.00m, "London killzone microstructure edge");
        }

        if (hour is >= 15 and < 18)
        {
            return new SessionGate(true, 0.95m, 0.90m, "New York killzone edge");
        }

        return new SessionGate(false, 0.65m, 0.50m, "Outside preferred HFT killzones");
    }

    private readonly record struct SessionGate(bool AllowTrade, decimal ScoreMultiplier, decimal SizeMultiplier, string Reason);
}
