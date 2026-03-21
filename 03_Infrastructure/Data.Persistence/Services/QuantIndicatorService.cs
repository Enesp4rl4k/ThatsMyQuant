using Microsoft.Extensions.Logging;
using Skender.Stock.Indicators;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Domain.Entities;
using ThatsMyQuant.Domain.Enums;
using ThatsMyQuant.Domain.Interfaces;
using ThatsMyQuant.Domain.ValueObjects;
using ThatsMyQuant.Infrastructure.Indicators;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Enterprise-grade technical analysis service using Skender.Stock.Indicators,
/// AlphaTrend (KivancOzbilgic), and Absorbing System (only_fibonacci).
/// </summary>
public class QuantIndicatorService : ITechnicalAnalysisService
{
    private readonly ILogger<QuantIndicatorService> _logger;
    private readonly IReadOnlyList<IStrategyRouter> _strategyRouters;
    private const int MinimumCandles = 52;

    public QuantIndicatorService(ILogger<QuantIndicatorService> logger, IEnumerable<IStrategyRouter>? strategyRouters = null)
    {
        _logger = logger;
        _strategyRouters = strategyRouters?.ToList()
            ?? new List<IStrategyRouter>
            {
                new HftStrategyRouter(),
                new TrendStrategyRouter(),
                new RangeStrategyRouter(),
                new BreakoutStrategyRouter()
            };
    }

    public Task<IndicatorSnapshot> ComputeIndicatorsAsync(List<Candle> candles)
    {
        if (candles.Count < MinimumCandles)
        {
            _logger.LogWarning("Not enough candles ({Count}) for reliable indicators. Minimum: {Min}",
                candles.Count, MinimumCandles);
            return Task.FromResult(new IndicatorSnapshot
            {
                CurrentPrice = candles.LastOrDefault()?.Close ?? 0,
                ComputedAt = DateTime.UtcNow
            });
        }

        // Convert to Skender Quote format
        var quotes = candles
            .Select(c => new Quote
            {
                Date = c.Timestamp,
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume
            })
            .OrderBy(q => q.Date)
            .ToList();

        // --- Standard Indicators (Skender) ---
        var rsiResults = quotes.GetRsi(14).ToList();
        var macdResults = quotes.GetMacd(12, 26, 9).ToList();
        var ema20Results = quotes.GetEma(20).ToList();
        var ema50Results = quotes.GetEma(50).ToList();
        var bbResults = quotes.GetBollingerBands(20, 2).ToList();
        var atrResults = quotes.GetAtr(14).ToList();

        var latestRsi = rsiResults.LastOrDefault();
        var latestMacd = macdResults.LastOrDefault();
        var latestEma20 = ema20Results.LastOrDefault();
        var latestEma50 = ema50Results.LastOrDefault();
        var latestBb = bbResults.LastOrDefault();
        var latestAtr = atrResults.LastOrDefault();

        // Volume ratio
        var volumeValues = candles.Select(c => (double)c.Volume).ToList();
        double currentVolume = volumeValues.Last();
        double avgVolume = volumeValues.Skip(Math.Max(0, volumeValues.Count - 20)).Take(20).Average();
        double volumeRatio = avgVolume > 0 ? currentVolume / avgVolume : 1.0;

        // --- AlphaTrend (KivancOzbilgic) ---
        var alphaTrendResults = AlphaTrendCalculator.Calculate(candles, coeff: 1.0, period: 14);
        var latestAlphaTrend = alphaTrendResults.LastOrDefault();
        var pmaxResults = PMaxCalculator.Calculate(candles, atrLength: 10, atrMultiplier: 3.0, maLength: 10);
        var latestPMax = pmaxResults.LastOrDefault();

        // --- Absorbing System (Support/Resistance) ---
        var srLevels = AbsorbingSystemCalculator.Calculate(candles, pivotLen: 10);
        var (nearestSupport, nearestResistance) = AbsorbingSystemCalculator.GetNearestLevels(srLevels, candles.Last().Close);

        // --- Algorithmic Price Action (Patterns) ---
        var detectedPattern = ThatsMyQuant.Application.Strategy.CandlestickPatternDetector.DetectPattern(candles);

        var snapshot = new IndicatorSnapshot
        {
            RSI = latestRsi?.Rsi,
            MACD = latestMacd?.Macd,
            MACDSignal = latestMacd?.Signal,
            MACDHistogram = latestMacd?.Histogram,
            EMA20 = latestEma20?.Ema,
            EMA50 = latestEma50?.Ema,
            BollingerUpper = latestBb?.UpperBand,
            BollingerMiddle = latestBb?.Sma,
            BollingerLower = latestBb?.LowerBand,
            ATR = latestAtr?.Atr,
            VolumeRatio = volumeRatio,
            AlphaTrend = latestAlphaTrend,
            PMax = latestPMax,
            SupportResistanceLevels = srLevels,
            NearestSupport = nearestSupport,
            NearestResistance = nearestResistance,
            CurrentPrice = candles.Last().Close,
            DetectedPattern = detectedPattern,
            ComputedAt = DateTime.UtcNow
        };

        _logger.LogInformation(
            "Indicators: RSI={RSI:F1} MACD={MACD:F4} EMA20={EMA20:F2} EMA50={EMA50:F2} ATR={ATR:F2} Vol={Vol:F1}x AT={AT} PMAX={PMAX} PA={PA} S/R={SR}",
            snapshot.RSI, snapshot.MACD, snapshot.EMA20, snapshot.EMA50,
            snapshot.ATR, snapshot.VolumeRatio,
            latestAlphaTrend?.IsBullish == true ? "BULLISH" : "BEARISH",
            latestPMax?.IsBullish == true ? "BULLISH" : "BEARISH",
            snapshot.DetectedPattern,
            $"S:{nearestSupport?.Price:F2}|R:{nearestResistance?.Price:F2}");

        return Task.FromResult(snapshot);
    }

    public async Task<TradeDecision> AnalyzeAsync(Signal signal, List<Candle> candles, RiskParameters risk)
    {
        _logger.LogInformation("Starting full analysis for {Symbol}...", signal.Symbol);

        var indicators = await ComputeIndicatorsAsync(candles);

        if (indicators.RSI is null || indicators.MACD is null)
            return TradeDecision.Reject("Insufficient data for reliable indicator computation.", indicators);

        var regime = MarketRegimeDetector.Detect(candles);
        var market = ResolveMarket(signal.Symbol);
        var sessionGate = EvaluateSessionGate(market, candles.Last().Timestamp, regime.Regime);

        // --- Confluence Strategy (expanded: 7 indicators, max 140 points) ---
        var (buyScore, sellScore, reasons) = EvaluateConfluence(indicators, regime);

        var bestSide = buyScore >= sellScore ? TradeSide.Buy : TradeSide.Sell;
        var bestScore = Math.Max(buyScore, sellScore);

        // Normalize to 0-100 range (max raw = 140, scale down)
        int normalizedScore = Math.Min(100, (int)(bestScore * 100.0 / 140.0));
        normalizedScore = Math.Max(0, (int)Math.Round(normalizedScore * (double)sessionGate.ScoreMultiplier));

        _logger.LogInformation("Confluence: BuyScore={Buy}/140, SellScore={Sell}/140, Normalized={Norm}/100 ({Side})",
            buyScore, sellScore, normalizedScore, bestSide);

        var router = ResolveRouter(market, signal, regime);
        var entryThreshold = router.EntryThreshold;

        if (!sessionGate.AllowTrade)
        {
            var sessionReason = $"Session gate blocked trade: {sessionGate.Reason}";
            _logger.LogWarning("Trade REJECTED: {Reason}", sessionReason);
            return TradeDecision.Reject(sessionReason, indicators);
        }

        var tradeabilityGate = EvaluateTradeabilityGate(indicators, regime.Regime, bestSide);
        if (!tradeabilityGate.Passed)
        {
            var gateReason = $"Tradeability gate failed: {tradeabilityGate.Reason}";
            _logger.LogWarning("Trade REJECTED: {Reason}", gateReason);
            return TradeDecision.Reject(gateReason, indicators);
        }

        if (normalizedScore < entryThreshold)
        {
            var rejectReason = $"Confluence too weak ({normalizedScore}/100, needs {entryThreshold}). {string.Join(" | ", reasons)}";
            _logger.LogWarning("Trade REJECTED: {Reason}", rejectReason);
            return TradeDecision.Reject(rejectReason, indicators);
        }

        // --- Risk Management ---
        decimal currentPrice = indicators.CurrentPrice;
        double atr = indicators.ATR ?? (double)currentPrice * 0.02;

        // Kelly Criterion
        double b = 1.5;
        double p = normalizedScore / 100.0;
        double q = 1.0 - p;
        double kellyFraction = Math.Max(0, (b * p - q) / b);
        decimal basePositionSize = Math.Min((decimal)kellyFraction, risk.MaxPositionSizePercent / 100m);
        decimal positionSize = AdjustPositionSize(basePositionSize, indicators, regime.Regime, sessionGate);

        var routedDecision = router.BuildDecision(new StrategyRouterContext(
            signal,
            indicators,
            regime,
            bestSide,
            normalizedScore,
            sessionGate.Reason,
            tradeabilityGate.Reason,
            positionSize,
            currentPrice,
            (decimal)atr,
            reasons));

        _logger.LogInformation("Trade APPROVED via {Router}: {Reasoning}", router.StrategyTag, routedDecision.Reasoning);

        return routedDecision;
    }

    private static (bool Passed, string Reason) EvaluateTradeabilityGate(
        IndicatorSnapshot ind,
        MarketRegime regime,
        TradeSide side)
    {
        var atr = Convert.ToDecimal(ind.ATR ?? (double)(ind.CurrentPrice * 0.02m));
        var supportRoom = ind.NearestSupport?.Price is decimal support
            ? ind.CurrentPrice - support
            : atr * 2m;
        var resistanceRoom = ind.NearestResistance?.Price is decimal resistance
            ? resistance - ind.CurrentPrice
            : atr * 2m;

        return regime switch
        {
            MarketRegime.Trending => EvaluateTrendingGate(ind, side, resistanceRoom, supportRoom, atr),
            MarketRegime.Ranging => EvaluateRangeGate(ind, side, resistanceRoom, supportRoom, atr),
            MarketRegime.Volatile => EvaluateVolatilityGate(ind, side, resistanceRoom, supportRoom, atr),
            _ => (true, "Fallback tradeability gate passed.")
        };
    }

    private static (bool Passed, string Reason) EvaluateTrendingGate(
        IndicatorSnapshot ind,
        TradeSide side,
        decimal resistanceRoom,
        decimal supportRoom,
        decimal atr)
    {
        var trendBullish = (ind.EMA20 ?? 0) > (ind.EMA50 ?? 0) && (ind.PMax?.IsBullish ?? false);
        var trendBearish = (ind.EMA20 ?? 0) < (ind.EMA50 ?? 0) && !(ind.PMax?.IsBullish ?? true);
        var roomOk = side == TradeSide.Buy ? resistanceRoom >= atr * 0.9m : supportRoom >= atr * 0.9m;

        if (side == TradeSide.Buy && !trendBullish)
            return (false, "Trend regime requires bullish EMA/PMax alignment.");
        if (side == TradeSide.Sell && !trendBearish)
            return (false, "Trend regime requires bearish EMA/PMax alignment.");
        if (!roomOk)
            return (false, "Trend regime lacks enough room before the nearest opposing level.");

        return (true, "Trend regime alignment and structure room passed.");
    }

    private static (bool Passed, string Reason) EvaluateRangeGate(
        IndicatorSnapshot ind,
        TradeSide side,
        decimal resistanceRoom,
        decimal supportRoom,
        decimal atr)
    {
        var rsi = ind.RSI ?? 50;
        var buyOk = supportRoom <= atr * 1.4m && rsi <= 45;
        var sellOk = resistanceRoom <= atr * 1.4m && rsi >= 55;

        if (side == TradeSide.Buy && !buyOk)
            return (false, "Range regime buy needs proximity to support and softer RSI.");
        if (side == TradeSide.Sell && !sellOk)
            return (false, "Range regime sell needs proximity to resistance and stretched RSI.");

        return (true, "Range regime mean-reversion gate passed.");
    }

    private static (bool Passed, string Reason) EvaluateVolatilityGate(
        IndicatorSnapshot ind,
        TradeSide side,
        decimal resistanceRoom,
        decimal supportRoom,
        decimal atr)
    {
        var volumeOk = (ind.VolumeRatio ?? 0) >= 1.8;
        var momentumOk = side == TradeSide.Buy
            ? (ind.MACDHistogram ?? 0) > 0 && (ind.EMA20 ?? 0) >= (ind.EMA50 ?? 0)
            : (ind.MACDHistogram ?? 0) < 0 && (ind.EMA20 ?? 0) <= (ind.EMA50 ?? 0);
        var roomOk = side == TradeSide.Buy ? resistanceRoom >= atr * 1.1m : supportRoom >= atr * 1.1m;

        if (!volumeOk)
            return (false, "Volatility regime needs stronger relative volume.");
        if (!momentumOk)
            return (false, "Volatility regime needs momentum aligned with breakout direction.");
        if (!roomOk)
            return (false, "Volatility regime lacks enough expansion room.");

        return (true, "Volatility breakout gate passed.");
    }

    private static decimal AdjustPositionSize(
        decimal basePositionSize,
        IndicatorSnapshot indicators,
        MarketRegime regime,
        SessionGate sessionGate)
    {
        decimal regimeMultiplier = regime switch
        {
            MarketRegime.Trending => 1.00m,
            MarketRegime.Ranging => 0.75m,
            MarketRegime.Volatile => 0.55m,
            _ => 0.80m
        };

        decimal volatilityPenalty = indicators.ATR is > 0 && indicators.CurrentPrice > 0
            ? Math.Clamp(1m - (Convert.ToDecimal(indicators.ATR.Value) / indicators.CurrentPrice), 0.55m, 1.00m)
            : 0.85m;

        decimal volumePenalty = (indicators.VolumeRatio ?? 1.0) switch
        {
            >= 1.8 => 1.00m,
            >= 1.2 => 0.85m,
            >= 1.0 => 0.70m,
            _ => 0.50m
        };

        var adjusted = basePositionSize * regimeMultiplier * sessionGate.SizeMultiplier * volatilityPenalty * volumePenalty;
        return Math.Clamp(adjusted, 0.0025m, basePositionSize);
    }

    private static string ResolveMarket(string symbol)
    {
        if (symbol.Contains("-", StringComparison.OrdinalIgnoreCase)) return "Crypto";
        if (symbol.Contains("=", StringComparison.OrdinalIgnoreCase)) return "Forex";
        return "BIST";
    }

    private IStrategyRouter ResolveRouter(string market, Signal signal, MarketRegimeDetector.RegimeResult regime)
    {
        return _strategyRouters.FirstOrDefault(router => router.CanHandle(market, signal, regime))
               ?? _strategyRouters.OfType<TrendStrategyRouter>().First();
    }

    private static SessionGate EvaluateSessionGate(string market, DateTime timestamp, MarketRegime regime)
    {
        if (string.Equals(market, "BIST", StringComparison.OrdinalIgnoreCase))
        {
            return new SessionGate(true, 1.00m, 1.00m, "Primary BIST session");
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");
        var hour = turkeyTime.Hour;

        if (hour is >= 10 and < 13)
            return new SessionGate(true, 1.00m, 1.00m, "London killzone session edge");
        if (hour is >= 15 and < 18)
            return new SessionGate(true, 0.95m, 0.90m, "New York killzone session edge");
        if (hour is >= 3 and < 10)
            return regime == MarketRegime.Volatile
                ? new SessionGate(false, 0.70m, 0.55m, "Asia handoff blocked for volatile breakout trades")
                : new SessionGate(true, 0.80m, 0.70m, "Asia handoff session: reduced conviction");

        return new SessionGate(false, 0.60m, 0.45m, "Dead-zone session: avoid fresh trades");
    }

    private readonly record struct SessionGate(bool AllowTrade, decimal ScoreMultiplier, decimal SizeMultiplier, string Reason);

    /// <summary>
    /// Multi-indicator confluence scoring (7 indicators, max 140 raw points, normalized to 0-100).
    /// </summary>
    private static (int buyScore, int sellScore, List<string> reasons) EvaluateConfluence(
        IndicatorSnapshot ind,
        MarketRegimeDetector.RegimeResult regime)
    {
        int buyScore = 0, sellScore = 0;
        var reasons = new List<string>();
        double price = (double)ind.CurrentPrice;

        // --- 1. RSI (20 points) ---
        if (ind.RSI.HasValue)
        {
            if (ind.RSI < 30)
            {
                buyScore += 20;
                reasons.Add($"RSI={ind.RSI:F1} (oversold ✅BUY)");
            }
            else if (ind.RSI >= 30 && ind.RSI <= 50)
            {
                buyScore += 15;
                reasons.Add($"RSI={ind.RSI:F1} (recovering ✅BUY)");
            }
            else if (ind.RSI > 70)
            {
                sellScore += 20;
                reasons.Add($"RSI={ind.RSI:F1} (overbought ✅SELL)");
            }
            else if (ind.RSI >= 50 && ind.RSI <= 70)
            {
                sellScore += 10;
                reasons.Add($"RSI={ind.RSI:F1} (neutral-high)");
            }
        }

        // --- 2. MACD (25 points) ---
        if (ind.MACDHistogram.HasValue)
        {
            if (ind.MACDHistogram > 0 && ind.MACD > ind.MACDSignal)
            {
                buyScore += 25;
                reasons.Add($"MACD histogram+ ({ind.MACDHistogram:F4} ✅BUY)");
            }
            else if (ind.MACDHistogram < 0 && ind.MACD < ind.MACDSignal)
            {
                sellScore += 25;
                reasons.Add($"MACD histogram- ({ind.MACDHistogram:F4} ✅SELL)");
            }
        }

        // --- 3. EMA Crossover (20 points) ---
        if (ind.EMA20.HasValue && ind.EMA50.HasValue)
        {
            if (price > ind.EMA20 && ind.EMA20 > ind.EMA50)
            {
                buyScore += 20;
                reasons.Add("EMA golden trend ✅BUY");
            }
            else if (price < ind.EMA20 && ind.EMA20 < ind.EMA50)
            {
                sellScore += 20;
                reasons.Add("EMA death trend ✅SELL");
            }
        }

        // --- 4. Bollinger Bands (15 points) ---
        if (ind.BollingerLower.HasValue && ind.BollingerUpper.HasValue)
        {
            double bbWidth = ind.BollingerUpper.Value - ind.BollingerLower.Value;
            double lowerThreshold = ind.BollingerLower.Value + bbWidth * 0.1;
            double upperThreshold = ind.BollingerUpper.Value - bbWidth * 0.1;

            if (price <= lowerThreshold)
            {
                buyScore += 15;
                reasons.Add($"BB lower bounce ({ind.BollingerLower:F2} ✅BUY)");
            }
            else if (price >= upperThreshold)
            {
                sellScore += 15;
                reasons.Add($"BB upper touch ({ind.BollingerUpper:F2} ✅SELL)");
            }
        }

        // --- 5. Volume Confirmation (20 points) ---
        if (ind.VolumeRatio.HasValue)
        {
            if (ind.VolumeRatio >= 1.5)
            {
                if (buyScore >= sellScore)
                {
                    buyScore += 20;
                    reasons.Add($"Volume {ind.VolumeRatio:F1}x ✅BUY confirmed");
                }
                else
                {
                    sellScore += 20;
                    reasons.Add($"Volume {ind.VolumeRatio:F1}x ✅SELL confirmed");
                }
            }
            else
            {
                reasons.Add($"Volume {ind.VolumeRatio:F1}x (low)");
            }
        }

        // --- 6. AlphaTrend (25 points) ---
        if (ind.AlphaTrend != null)
        {
            if (ind.AlphaTrend.Signal == TradeSide.Buy && ind.AlphaTrend.IsConfirmed)
            {
                buyScore += 25;
                reasons.Add("AlphaTrend BUY confirmed ✅BUY");
            }
            else if (ind.AlphaTrend.Signal == TradeSide.Sell && ind.AlphaTrend.IsConfirmed)
            {
                sellScore += 25;
                reasons.Add("AlphaTrend SELL confirmed ✅SELL");
            }
            else if (ind.AlphaTrend.IsBullish)
            {
                buyScore += 10;
                reasons.Add("AlphaTrend bullish (no signal)");
            }
            else
            {
                sellScore += 10;
                reasons.Add("AlphaTrend bearish (no signal)");
            }
        }

        if (ind.PMax != null)
        {
            if (ind.PMax.Signal == TradeSide.Buy || (ind.PMax.IsBullish && ind.CurrentPrice > (decimal)ind.PMax.Value))
            {
                buyScore += 15;
                reasons.Add("PMax bullish filter confirmed");
            }
            else if (ind.PMax.Signal == TradeSide.Sell || (!ind.PMax.IsBullish && ind.CurrentPrice < (decimal)ind.PMax.Value))
            {
                sellScore += 15;
                reasons.Add("PMax bearish filter confirmed");
            }
        }

        // --- 7. Support/Resistance Proximity (15 points) ---
        if (ind.NearestSupport != null || ind.NearestResistance != null)
        {
            if (ind.NearestSupport != null)
            {
                decimal distToSupport = Math.Abs(ind.CurrentPrice - ind.NearestSupport.Price) / ind.CurrentPrice;
                if (distToSupport < 0.02m) // Within 2% of support
                {
                    buyScore += 15;
                    reasons.Add($"Near S/R support {ind.NearestSupport.Price:F2} (tested {ind.NearestSupport.TestCount}x ✅BUY)");
                }
            }
            if (ind.NearestResistance != null)
            {
                decimal distToResistance = Math.Abs(ind.NearestResistance.Price - ind.CurrentPrice) / ind.CurrentPrice;
                if (distToResistance < 0.02m) // Within 2% of resistance
                {
                    sellScore += 15;
                    reasons.Add($"Near S/R resistance {ind.NearestResistance.Price:F2} (tested {ind.NearestResistance.TestCount}x ✅SELL)");
                }
            }
        }

        // --- 8. Price Action Patterns (25 points) ---
        if (ind.DetectedPattern != "None")
        {
            if (ind.DetectedPattern.Contains("Bullish", StringComparison.OrdinalIgnoreCase))
            {
                buyScore += 25;
                reasons.Add($"Pattern: {ind.DetectedPattern} ✅BUY");
            }
            else if (ind.DetectedPattern.Contains("Bearish", StringComparison.OrdinalIgnoreCase))
            {
                sellScore += 25;
                reasons.Add($"Pattern: {ind.DetectedPattern} ✅SELL");
            }
            else
            {
                // Neutral patterns like Standard Doji might just indicate indecision
                reasons.Add($"Pattern: {ind.DetectedPattern} (Neutral)");
            }
        }

        if (regime.Regime == MarketRegime.Trending)
        {
            if (ind.EMA20 > ind.EMA50 && ind.MACDHistogram > 0)
            {
                buyScore += 10;
                reasons.Add("Trend regime bonus: trend + momentum aligned");
            }
            if (ind.EMA20 < ind.EMA50 && ind.MACDHistogram < 0)
            {
                sellScore += 10;
                reasons.Add("Trend regime bonus: downside trend aligned");
            }
        }
        else if (regime.Regime == MarketRegime.Ranging)
        {
            if (ind.RSI is > 40 and < 60)
            {
                reasons.Add("Range regime filter: RSI neutral band preferred");
            }
            else
            {
                buyScore -= 5;
                sellScore -= 5;
                reasons.Add("Range regime penalty: RSI stretched for mean reversion");
            }
        }
        else if (regime.Regime == MarketRegime.Volatile)
        {
            if ((ind.VolumeRatio ?? 0) < 1.8)
            {
                buyScore -= 10;
                sellScore -= 10;
                reasons.Add("Volatility regime penalty: breakout lacks strong volume");
            }
            else
            {
                reasons.Add("Volatility regime filter passed: elevated volume present");
            }
        }

        return (buyScore, sellScore, reasons);
    }
}
