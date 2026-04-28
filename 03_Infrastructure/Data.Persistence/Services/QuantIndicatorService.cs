using Microsoft.Extensions.Logging;
using Skender.Stock.Indicators;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Infrastructure.Indicators;
using System.Runtime.InteropServices;

namespace ThatsMyQuant.Infrastructure.Services;

/// <summary>
/// Enterprise-grade technical analysis service using Skender.Stock.Indicators,
/// AlphaTrend (KivancOzbilgic), and Absorbing System (only_fibonacci).
/// </summary>
public class QuantIndicatorService : ITechnicalAnalysisService
{
    private readonly ILogger<QuantIndicatorService> _logger;
    private readonly IReadOnlyList<IStrategyRouter> _strategyRouters;
    private readonly ExitPolicyLearningService? _exitPolicyLearningService;
    private readonly MultiTimeframeConfluenceService? _mtfService;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Domain.Common.CircularBuffer<Candle>> _hftCandleCache = new();
    private const int MinimumCandles = 52;

    public QuantIndicatorService(
        ILogger<QuantIndicatorService> logger,
        IEnumerable<IStrategyRouter>? strategyRouters = null,
        ExitPolicyLearningService? exitPolicyLearningService = null,
        MultiTimeframeConfluenceService? mtfService = null,
        MarketExecutionProfileService? executionProfiles = null)
    {
        _logger = logger;
        _exitPolicyLearningService = exitPolicyLearningService;
        _mtfService = mtfService;
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _strategyRouters = strategyRouters?.ToList()
            ?? new List<IStrategyRouter>
            {
                new HftStrategyRouter(),
                new TrendStrategyRouter(),
                new RangeStrategyRouter(),
                new BreakoutStrategyRouter()
            };
    }

    /// <summary>
    /// HFT-optimized update for a single candle to maintain online indicator state.
    /// </summary>
    public void UpdateHftState(string symbol, Candle candle)
    {
        var buffer = _hftCandleCache.GetOrAdd(symbol, _ => new Domain.Common.CircularBuffer<Candle>(200));
        buffer.Add(candle);
    }

    public Task<IndicatorSnapshot> ComputeIndicatorsAsync(IReadOnlyList<Candle> candles)
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
        // 2026-04-24 alpha expansion: ADX/DI for trend-strength, ROC for momentum,
        // StochRSI for mean-reversion pivots. Fills the feature gap left by zeroed
        // MACD/AlphaTrend/PMax (all IC-failed).
        var adxResults = quotes.GetAdx(14).ToList();
        var rocResults = quotes.GetRoc(10).ToList();
        var stochRsiResults = quotes.GetStochRsi(14, 14, 3, 3).ToList();

        var latestRsi = rsiResults.LastOrDefault();
        var latestMacd = macdResults.LastOrDefault();
        var latestEma20 = ema20Results.LastOrDefault();
        var latestEma50 = ema50Results.LastOrDefault();
        var latestBb = bbResults.LastOrDefault();
        var latestAtr = atrResults.LastOrDefault();
        var latestAdx = adxResults.LastOrDefault();
        var latestRoc = rocResults.LastOrDefault();
        var latestStochRsi = stochRsiResults.LastOrDefault();

        // Volume ratio
        var volumeValues = candles.Select(c => (double)c.Volume).ToList();
        double currentVolume = volumeValues.Last();
        double avgVolume = volumeValues.Skip(Math.Max(0, volumeValues.Count - 20)).Take(20).Average();
        double volumeRatio = avgVolume > 0 ? currentVolume / avgVolume : 1.0;

        // --- AlphaTrend (KivancOzbilgic) ---
        // HFT Optimization: Using Span and stackalloc for small indicator result sets
        var candleSpan = candles is List<Candle> list ? CollectionsMarshal.AsSpan(list) : candles.ToArray().AsSpan();
        
        var alphaTrendResults = new AlphaTrendResult[candles.Count];
        AlphaTrendCalculator.Calculate(candleSpan, alphaTrendResults, coeff: 1.0, period: 14);
        var latestAlphaTrend = alphaTrendResults[^1];

        var pmaxResults = new PMaxResult[candles.Count];
        PMaxCalculator.Calculate(candleSpan, pmaxResults, atrLength: 10, atrMultiplier: 3.0, maLength: 10);
        var latestPMax = pmaxResults[^1];

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
            ADX = latestAdx?.Adx,
            DIPlus = latestAdx?.Pdi,
            DIMinus = latestAdx?.Mdi,
            ROC = latestRoc?.Roc,
            StochRsiK = latestStochRsi?.StochRsi,
            StochRsiD = latestStochRsi?.Signal,
            VolumeRatio = volumeRatio,
            AlphaTrend = latestAlphaTrend,
            PMax = latestPMax,
            SupportResistanceLevels = srLevels,
            NearestSupport = nearestSupport,
            NearestResistance = nearestResistance,
            BarOpen = candles.Last().Open,
            CurrentPrice = candles.Last().Close,
            DetectedPattern = detectedPattern,
            ComputedAt = DateTime.UtcNow
        };

        _logger.LogInformation(
            "Indicators: RSI={RSI:F1} MACD={MACD:F4} EMA20={EMA20:F2} EMA50={EMA50:F2} ATR={ATR:F2} Vol={Vol:F1}x AT={AT} PMAX={PMAX} PA={PA} S/R={SR}",
            snapshot.RSI, snapshot.MACD, snapshot.EMA20, snapshot.EMA50,
            snapshot.ATR, snapshot.VolumeRatio,
            latestAlphaTrend.IsBullish ? "BULLISH" : "BEARISH",
            latestPMax.IsBullish ? "BULLISH" : "BEARISH",
            snapshot.DetectedPattern,
            $"S:{nearestSupport?.Price:F2}|R:{nearestResistance?.Price:F2}");

        return Task.FromResult(snapshot);
    }

    public async Task<TradeDecision> AnalyzeAsync(Signal signal, IReadOnlyList<Candle> candles, RiskParameters risk)
    {
        _logger.LogInformation("Starting full analysis for {Symbol}...", signal.Symbol);

        var indicators = await ComputeIndicatorsAsync(candles);

        if (indicators.RSI is null || indicators.MACD is null)
            return TradeDecision.Reject("Insufficient data for reliable indicator computation.", indicators);

        var regime = MarketRegimeDetector.Detect(candles);
        var market = ResolveMarket(signal.Symbol);
        var sessionGate = EvaluateSessionGate(market, candles.Last().Timestamp, regime.Regime);

        // --- Confluence Strategy ---
        var (buyScore, sellScore, reasons) = EvaluateConfluence(indicators, regime);

        var bestSide = buyScore >= sellScore ? TradeSide.Buy : TradeSide.Sell;
        var bestScore = Math.Max(buyScore, sellScore);

        // Keep alpha scoring independent from session timing so valid setups are not
        // double-penalized by both confidence and position sizing.
        // 2026-04-24: denominator tightened from 105 -> 85 after zeroing weak-alpha
        // contributors so validated BB/SR/EMA setups are not under-scored.
        int normalizedScore = Math.Min(100, (int)(bestScore * 100.0 / 85.0));

        _logger.LogInformation("Confluence: BuyScore={Buy}/140, SellScore={Sell}/140, Normalized={Norm}/100 ({Side})",
            buyScore, sellScore, normalizedScore, bestSide);

        var router = ResolveRouter(market, signal, regime);
        var entryThreshold = router.EntryThreshold;
        var executionProfile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);

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

        var alphaQualityGate = EvaluateAlphaQualityGate(indicators, regime.Regime, bestSide);
        if (!alphaQualityGate.Passed)
        {
            var gateReason = $"Alpha quality gate failed: {alphaQualityGate.Reason}";
            _logger.LogWarning("Trade REJECTED: {Reason}", gateReason);
            return TradeDecision.Reject(gateReason, indicators);
        }

        var entryConfluence = EntryConfluenceService.Evaluate(candles, bestSide, regime.Regime);
        if (!entryConfluence.Pass)
        {
            var gateReason = $"Entry confluence gate failed: {entryConfluence.RejectReason}";
            _logger.LogWarning("Trade REJECTED: {Reason}", gateReason);
            return TradeDecision.Reject(gateReason, indicators);
        }

        if (normalizedScore < entryThreshold)
        {
            var rejectReason = $"Confluence too weak ({normalizedScore}/100, needs {entryThreshold}). {string.Join(" | ", reasons)}";
            _logger.LogWarning("Trade REJECTED: {Reason}", rejectReason);
            return TradeDecision.Reject(rejectReason, indicators);
        }

        // P1-A: MTF Confluence Gate
        if (_mtfService != null && candles.Count >= 20)
        {
            var mtfResult = _mtfService.EvaluateFromBase(candles);
            if (mtfResult.Score < 45)
            {
                var rejectReason = $"MTF Confluence too weak ({mtfResult.Score}/100 < 45). {mtfResult.Summary}";
                return TradeDecision.Reject(rejectReason, indicators);
            }

            bool isAligned = bestSide == TradeSide.Buy 
                ? mtfResult.Snapshots.All(s => s.BiasScore >= -0.02) 
                : mtfResult.Snapshots.All(s => s.BiasScore <= 0.02);

            if (!isAligned)
            {
                var rejectReason = $"MTF Trend Alignment failed for {bestSide}. {mtfResult.Summary}";
                return TradeDecision.Reject(rejectReason, indicators);
            }
            reasons.Add($"MTF Score {mtfResult.Score}/100");
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
            executionProfile,
            bestSide,
            normalizedScore,
            sessionGate.Reason,
            tradeabilityGate.Reason,
            positionSize,
            currentPrice,
            (decimal)atr,
            EstimateRoundTripCostPercent(executionProfile),
            reasons));

        var learnedDecision = _exitPolicyLearningService != null
            ? await _exitPolicyLearningService.ApplyLearnedPolicy(signal, routedDecision, currentPrice, (decimal)atr)
            : routedDecision;
        learnedDecision = _exitPolicyLearningService != null
            ? await _exitPolicyLearningService.ApplyLiveSizingConfidence(signal, learnedDecision)
            : learnedDecision;

        if (!string.Equals(learnedDecision.ExitPolicyTag, routedDecision.ExitPolicyTag, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Exit learning override for {Symbol}: {FromPolicy} -> {ToPolicy}",
                signal.Symbol,
                routedDecision.ExitPolicyTag,
                learnedDecision.ExitPolicyTag);
        }

        _logger.LogInformation("Trade APPROVED via {Router}: {Reasoning}", router.StrategyTag, learnedDecision.Reasoning);

        return learnedDecision;
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

    private static (bool Passed, string Reason) EvaluateAlphaQualityGate(
        IndicatorSnapshot ind,
        MarketRegime regime,
        TradeSide side)
    {
        return regime switch
        {
            MarketRegime.Ranging => EvaluateRangeAlphaQuality(ind, side),
            MarketRegime.Trending => EvaluateTrendAlphaQuality(ind, side),
            MarketRegime.Volatile => EvaluateVolatileAlphaQuality(ind, side),
            _ => (true, "Fallback alpha quality gate passed.")
        };
    }

    private static (bool Passed, string Reason) EvaluateRangeAlphaQuality(IndicatorSnapshot ind, TradeSide side)
    {
        bool nearSupport = ind.NearestSupport is not null &&
                           Math.Abs(ind.CurrentPrice - ind.NearestSupport.Price) / ind.CurrentPrice <= 0.012m &&
                           ind.NearestSupport.TestCount >= 2;
        bool nearResistance = ind.NearestResistance is not null &&
                              Math.Abs(ind.NearestResistance.Price - ind.CurrentPrice) / ind.CurrentPrice <= 0.012m &&
                              ind.NearestResistance.TestCount >= 2;
        bool bbLong = ind.BollingerLower.HasValue && ind.BollingerUpper.HasValue &&
                      (double)ind.CurrentPrice <= ind.BollingerLower.Value + ((ind.BollingerUpper.Value - ind.BollingerLower.Value) * 0.1);
        bool bbShort = ind.BollingerLower.HasValue && ind.BollingerUpper.HasValue &&
                       (double)ind.CurrentPrice >= ind.BollingerUpper.Value - ((ind.BollingerUpper.Value - ind.BollingerLower.Value) * 0.1);
        double rsi = ind.RSI ?? 50;

        if (side == TradeSide.Buy)
        {
            if ((nearSupport || bbLong) && rsi <= 48) return (true, "Range alpha setup confirmed.");
            return (false, "Range long needs BB/SR support plus soft RSI.");
        }

        if ((nearResistance || bbShort) && rsi >= 52) return (true, "Range alpha setup confirmed.");
        return (false, "Range short needs BB/SR resistance plus stretched RSI.");
    }

    private static (bool Passed, string Reason) EvaluateTrendAlphaQuality(IndicatorSnapshot ind, TradeSide side)
    {
        if (!ind.EMA20.HasValue || !ind.EMA50.HasValue) return (false, "Trend alpha requires EMA alignment.");

        double rsi = ind.RSI ?? 50;
        bool volumeOk = (ind.VolumeRatio ?? 0) >= 0.9;
        // RSI ceiling raised from 62→73: aligns with EvaluateRsi which now scores RSI 55-73
        // as bullish continuation. Previously the quality gate blocked setups that RSI scoring
        // correctly identified as trend-continuation longs (RSI 62-72 in an uptrend is healthy).
        bool longTrend = ind.EMA20 > ind.EMA50 && ind.CurrentPrice >= (decimal)ind.EMA20 && rsi is >= 40 and <= 73;
        bool shortTrend = ind.EMA20 < ind.EMA50 && ind.CurrentPrice <= (decimal)ind.EMA20 && rsi is >= 27 and <= 60;

        if (side == TradeSide.Buy)
            return longTrend && volumeOk
                ? (true, "Trend alpha setup confirmed.")
                : (false, "Trend long needs EMA alignment, pullback RSI and non-dead volume.");

        return shortTrend && volumeOk
            ? (true, "Trend alpha setup confirmed.")
            : (false, "Trend short needs EMA alignment, pullback RSI and non-dead volume.");
    }

    private static (bool Passed, string Reason) EvaluateVolatileAlphaQuality(IndicatorSnapshot ind, TradeSide side)
    {
        if (!ind.EMA20.HasValue || !ind.EMA50.HasValue) return (false, "Volatility alpha requires EMA alignment.");

        bool volumeOk = (ind.VolumeRatio ?? 0) >= 1.5;
        bool bbLong = ind.BollingerUpper.HasValue && (double)ind.CurrentPrice >= ind.BollingerUpper.Value * 0.995;
        bool bbShort = ind.BollingerLower.HasValue && (double)ind.CurrentPrice <= ind.BollingerLower.Value * 1.005;
        bool longTrend = ind.EMA20 >= ind.EMA50;
        bool shortTrend = ind.EMA20 <= ind.EMA50;

        if (side == TradeSide.Buy)
            return volumeOk && bbLong && longTrend
                ? (true, "Volatility alpha setup confirmed.")
                : (false, "Volatility long needs expansion volume, upper-band pressure and EMA support.");

        return volumeOk && bbShort && shortTrend
            ? (true, "Volatility alpha setup confirmed.")
            : (false, "Volatility short needs expansion volume, lower-band pressure and EMA support.");
    }

    private static (bool Passed, string Reason) EvaluateTrendingGate(
        IndicatorSnapshot ind,
        TradeSide side,
        decimal resistanceRoom,
        decimal supportRoom,
        decimal atr)
    {
        var trendBullish = (ind.EMA20 ?? 0) > (ind.EMA50 ?? 0);
        var trendBearish = (ind.EMA20 ?? 0) < (ind.EMA50 ?? 0);
        var roomOk = side == TradeSide.Buy ? resistanceRoom >= atr * 0.9m : supportRoom >= atr * 0.9m;

        if (side == TradeSide.Buy && !trendBullish)
            return (false, "Trend regime requires bullish EMA alignment.");
        if (side == TradeSide.Sell && !trendBearish)
            return (false, "Trend regime requires bearish EMA alignment.");
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
        bool bbLong = ind.BollingerLower.HasValue && ind.BollingerUpper.HasValue &&
                      (double)ind.CurrentPrice <= ind.BollingerLower.Value + ((ind.BollingerUpper.Value - ind.BollingerLower.Value) * 0.15);
        bool bbShort = ind.BollingerLower.HasValue && ind.BollingerUpper.HasValue &&
                       (double)ind.CurrentPrice >= ind.BollingerUpper.Value - ((ind.BollingerUpper.Value - ind.BollingerLower.Value) * 0.15);
        var buyOk = (supportRoom <= atr * 1.8m || bbLong) && rsi <= 50;
        var sellOk = (resistanceRoom <= atr * 1.8m || bbShort) && rsi >= 50;

        if (side == TradeSide.Buy && !buyOk)
            return (false, "Range regime buy needs BB/SR support and soft RSI.");
        if (side == TradeSide.Sell && !sellOk)
            return (false, "Range regime sell needs BB/SR resistance and stretched RSI.");

        return (true, "Range regime mean-reversion gate passed.");
    }

    private static (bool Passed, string Reason) EvaluateVolatilityGate(
        IndicatorSnapshot ind,
        TradeSide side,
        decimal resistanceRoom,
        decimal supportRoom,
        decimal atr)
    {
        var volumeOk = (ind.VolumeRatio ?? 0) >= 1.25;
        var momentumOk = side == TradeSide.Buy
            ? (ind.MACDHistogram ?? 0) > 0 && (ind.EMA20 ?? 0) >= (ind.EMA50 ?? 0)
            : (ind.MACDHistogram ?? 0) < 0 && (ind.EMA20 ?? 0) <= (ind.EMA50 ?? 0);
        var roomOk = side == TradeSide.Buy ? resistanceRoom >= atr * 0.9m : supportRoom >= atr * 0.9m;

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

        decimal volatilityPenalty = 0.85m;
        if (indicators.ATR is > 0 && indicators.CurrentPrice > 0)
        {
            decimal atrPct = Convert.ToDecimal(indicators.ATR.Value) / indicators.CurrentPrice;
            if (atrPct > 0)
            {
                // P5-A: TargetVolatility sizing. Inverse relationship to bar ATR%.
                decimal targetVol = 0.015m; 
                volatilityPenalty = Math.Clamp(targetVol / atrPct, 0.25m, 2.5m);
            }
        }

        decimal volumePenalty = (indicators.VolumeRatio ?? 1.0) switch
        {
            >= 1.5 => 1.00m,
            >= 1.15 => 0.85m,
            >= 0.9 => 0.70m,
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
            return new SessionGate(true, 1.00m, "Primary BIST session");
        }

        var turkeyTime = timestamp.Kind == DateTimeKind.Utc
            ? TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp, "Turkey Standard Time")
            : TimeZoneInfo.ConvertTimeBySystemTimeZoneId(timestamp.ToUniversalTime(), "Turkey Standard Time");
        var hour = turkeyTime.Hour;

        if (hour is >= 10 and < 13)
            return new SessionGate(true, 1.00m, "London killzone session edge");
        if (hour is >= 15 and < 18)
            return new SessionGate(true, 0.90m, "New York killzone session edge");
        if (hour is >= 3 and < 10)
            return regime == MarketRegime.Volatile
                ? new SessionGate(true, 0.55m, "Asia handoff session: volatile regime, reduced size")
                : new SessionGate(true, 0.70m, "Asia handoff session: reduced size");

        return new SessionGate(true, 0.35m, "Dead-zone session: reduced size only");
    }

    private readonly record struct SessionGate(bool AllowTrade, decimal SizeMultiplier, string Reason);

    private static decimal EstimateRoundTripCostPercent(MarketExecutionProfile profile)
        => profile.SpreadPercent + (profile.SlippagePercent * 2m) + (profile.CommissionPercentPerSide * 2m);

    /// <summary>
    /// Multi-indicator confluence scoring with weak-alpha blocks zeroed and
    /// higher weight on validated mean-reversion structure.
    /// </summary>
    private static (int buyScore, int sellScore, List<string> reasons) EvaluateConfluence(
        IndicatorSnapshot ind,
        MarketRegimeDetector.RegimeResult regime)
    {
        int buyScore = 0, sellScore = 0;
        var reasons = new List<string>();

        EvaluateRsi(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateMacd(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateEma(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateBollinger(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateVolume(ind, ref buyScore, ref sellScore, reasons);
        EvaluateAlphaTrend(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluatePMax(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateAdx(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateRoc(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateStochRsi(ind, regime.Regime, ref buyScore, ref sellScore, reasons);
        EvaluateSupportResistance(ind, ref buyScore, ref sellScore, reasons);
        EvaluatePattern(ind, ref buyScore, ref sellScore, reasons);
        ApplyRegimeConfluenceAdjustments(ind, regime, ref buyScore, ref sellScore, reasons);

        return (buyScore, sellScore, reasons);
    }

    private static void EvaluateRsi(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (!ind.RSI.HasValue) return;

        double rsi = ind.RSI.Value;

        if (regime == MarketRegime.Trending)
        {
            // In trending markets RSI measures momentum, NOT mean-reversion extremes.
            // RSI 50-73 is healthy trend continuation — must NOT score as sell.
            // Prior bug: RSI 50-70 added sellScore+25 here, causing bestSide to flip to
            // SELL in uptrends → EMA alignment gate rejected → NoTradeSignal (4300 rejects).
            if (rsi < 30)
            {
                // Deep oversold in trend — possible breakdown, small upward reflex possible.
                buyScore += 8;
                reasons.Add($"RSI={rsi:F1} (deep oversold in trend ✅BUY +8)");
            }
            else if (rsi <= 55)
            {
                // Pullback into ideal re-entry zone: trend momentum intact.
                buyScore += 22;
                reasons.Add($"RSI={rsi:F1} (trend pullback zone ✅BUY +22)");
            }
            else if (rsi <= 73)
            {
                // Continuation momentum — trend strongly on (RSI sustained above midpoint).
                buyScore += 12;
                reasons.Add($"RSI={rsi:F1} (trend continuation ✅BUY +12)");
            }
            else
            {
                // Extended above 73: potential exhaustion, minor sell pressure.
                sellScore += 8;
                reasons.Add($"RSI={rsi:F1} (trend overextended ✅SELL +8)");
            }
        }
        else
        {
            // Ranging / Volatile: classic mean-reversion RSI logic.
            if (rsi < 30)
            {
                buyScore += regime == MarketRegime.Volatile ? 20 : 30;
                reasons.Add($"RSI={rsi:F1} (oversold ✅BUY)");
            }
            else if (rsi <= 50)
            {
                buyScore += 15;
                reasons.Add($"RSI={rsi:F1} (recovering ✅BUY)");
            }
            else if (rsi > 70)
            {
                sellScore += regime == MarketRegime.Volatile ? 20 : 30;
                reasons.Add($"RSI={rsi:F1} (overbought ✅SELL)");
            }
            else
            {
                sellScore += 15;
                reasons.Add($"RSI={rsi:F1} (neutral-high ✅SELL)");
            }
        }
    }

    private static void EvaluateMacd(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        // 2026-04-24: IC audit shows histogram-direction scoring is anti-predictive (IC=-0.20).
        // MACD lags price — by the time histogram goes positive the move has already peaked.
        // Weight zeroed; value logged as telemetry only. Revisit when gradient/crossover-only
        // formulation (histogram slope, not direction) is validated with IC > 0.05.
        if (!ind.MACDHistogram.HasValue) return;
        reasons.Add($"MACD={ind.MACDHistogram:F4} (telemetry; weight=0)");
    }

    private static void EvaluateEma(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (!ind.EMA20.HasValue || !ind.EMA50.HasValue) return;

        double price = (double)ind.CurrentPrice;
        if (price > ind.EMA20 && ind.EMA20 > ind.EMA50)
        {
            if (regime == MarketRegime.Trending)
            {
                buyScore += 20;
                reasons.Add("EMA golden trend (Regime: Trending ✅BUY)");
            }
            else if (regime == MarketRegime.Ranging && price > ind.EMA20 * 1.02)
            {
                // In a ranging market, price significantly above moving averages is mean-reverting (overbought)
                sellScore += 15;
                reasons.Add("EMA stretched in Range (Mean Reversion ✅SELL)");
            }
            else
            {
                reasons.Add("EMA golden trend (IC zeroed due to Range/Neutral)");
            }
        }
        else if (price < ind.EMA20 && ind.EMA20 < ind.EMA50)
        {
            if (regime == MarketRegime.Trending)
            {
                sellScore += 20;
                reasons.Add("EMA death trend (Regime: Trending ✅SELL)");
            }
            else if (regime == MarketRegime.Ranging && price < ind.EMA20 * 0.98)
            {
                buyScore += 15;
                reasons.Add("EMA stretched in Range (Mean Reversion ✅BUY)");
            }
            else
            {
                reasons.Add("EMA death trend (IC zeroed due to Range/Neutral)");
            }
        }
    }

    private static void EvaluateBollinger(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (!ind.BollingerLower.HasValue || !ind.BollingerUpper.HasValue) return;

        double price = (double)ind.CurrentPrice;
        double bbWidth = ind.BollingerUpper.Value - ind.BollingerLower.Value;
        double lowerThreshold = ind.BollingerLower.Value + bbWidth * 0.1;
        double upperThreshold = ind.BollingerUpper.Value - bbWidth * 0.1;

        int points = regime switch
        {
            MarketRegime.Ranging => 30,
            MarketRegime.Trending => 20,
            MarketRegime.Volatile => 15,
            _ => 15
        };

        if (price <= lowerThreshold)
        {
            buyScore += points;
            reasons.Add($"BB lower bounce ({ind.BollingerLower:F2} ✅BUY)");
        }
        else if (price >= upperThreshold)
        {
            sellScore += points;
            reasons.Add($"BB upper touch ({ind.BollingerUpper:F2} ✅SELL)");
        }
    }

    private static void EvaluateVolume(IndicatorSnapshot ind, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (!ind.VolumeRatio.HasValue) return;

        if (ind.VolumeRatio >= 1.5)
        {
            // 2026-04-24: Replaced circular score-comparison (whichever side is winning gets
            // the volume boost) with bar-direction volume: high volume on a bullish close is
            // upside conviction; on a bearish close is downside conviction. Removes the
            // amplification-of-lagging-MACD feedback loop.
            bool isUp = ind.BarOpen > 0 && ind.CurrentPrice > ind.BarOpen;
            bool isDown = ind.BarOpen > 0 && ind.CurrentPrice < ind.BarOpen;
            if (isUp)
            {
                buyScore += 20;
                reasons.Add($"Volume {ind.VolumeRatio:F1}x bullish bar ✅BUY");
            }
            else if (isDown)
            {
                sellScore += 20;
                reasons.Add($"Volume {ind.VolumeRatio:F1}x bearish bar ✅SELL");
            }
            else
            {
                reasons.Add($"Volume {ind.VolumeRatio:F1}x doji bar (neutral)");
            }
        }
        else
        {
            reasons.Add($"Volume {ind.VolumeRatio:F1}x (low)");
        }
    }

    #pragma warning disable CS0162
    private static void EvaluateAlphaTrend(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.AlphaTrend == null) return;
        reasons.Add("AlphaTrend telemetry only (weight=0)");
        return;

        bool isTrending = regime == MarketRegime.Trending;

        if (ind.AlphaTrend.Signal == TradeSide.Buy && ind.AlphaTrend.IsConfirmed)
        {
            if (isTrending)
            {
                buyScore += 20;
                reasons.Add("AlphaTrend BUY confirmed (Regime: Trending ✅BUY)");
            }
            else
            {
                reasons.Add("AlphaTrend BUY confirmed (IC zeroed due to Range)");
            }
        }
        else if (ind.AlphaTrend.Signal == TradeSide.Sell && ind.AlphaTrend.IsConfirmed)
        {
            if (isTrending)
            {
                sellScore += 20;
                reasons.Add("AlphaTrend SELL confirmed (Regime: Trending ✅SELL)");
            }
            else
            {
                reasons.Add("AlphaTrend SELL confirmed (IC zeroed due to Range)");
            }
        }
        else if (ind.AlphaTrend.IsBullish)
        {
            if (isTrending) buyScore += 10;
            reasons.Add("AlphaTrend bullish bias");
        }
        else
        {
            if (isTrending) sellScore += 10;
            reasons.Add("AlphaTrend bearish bias");
        }
    }

    private static void EvaluatePMax(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.PMax == null) return;
        reasons.Add("PMax telemetry only (weight=0)");
        return;

        int points = regime == MarketRegime.Trending ? 20 : 10;

        if (ind.PMax.Signal == TradeSide.Buy || (ind.PMax.IsBullish && ind.CurrentPrice > (decimal)ind.PMax.Value))
        {
            buyScore += points;
            reasons.Add($"PMax bullish filter confirmed ({points} pts)");
        }
        else if (ind.PMax.Signal == TradeSide.Sell || (!ind.PMax.IsBullish && ind.CurrentPrice < (decimal)ind.PMax.Value))
        {
            sellScore += points;
            reasons.Add($"PMax bearish filter confirmed ({points} pts)");
        }
    }

    private static void EvaluateSupportResistance(IndicatorSnapshot ind, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.NearestSupport != null)
        {
            decimal distToSupport = Math.Abs(ind.CurrentPrice - ind.NearestSupport.Price) / ind.CurrentPrice;
            if (distToSupport <= 0.012m && ind.NearestSupport.TestCount >= 2) // Within 1.2% and tested >= 2
            {
                buyScore += 25;
                reasons.Add($"Near S/R support {ind.NearestSupport.Price:F2} (tested {ind.NearestSupport.TestCount}x ✅BUY)");
            }
        }
        if (ind.NearestResistance != null)
        {
            decimal distToResistance = Math.Abs(ind.NearestResistance.Price - ind.CurrentPrice) / ind.CurrentPrice;
            if (distToResistance <= 0.012m && ind.NearestResistance.TestCount >= 2) // Within 1.2% and tested >= 2
            {
                sellScore += 25;
                reasons.Add($"Near S/R resistance {ind.NearestResistance.Price:F2} (tested {ind.NearestResistance.TestCount}x ✅SELL)");
            }
        }
    }

    private static void EvaluatePattern(IndicatorSnapshot ind, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.DetectedPattern == "None") return;
        reasons.Add($"Pattern {ind.DetectedPattern} (telemetry; weight=0)");
        return;

        if (ind.DetectedPattern.Contains("Bullish", StringComparison.OrdinalIgnoreCase))
        {
            buyScore += 12; // Halved due to inverted IC@20
            reasons.Add($"Pattern {ind.DetectedPattern} ✅BUY");
        }
        else if (ind.DetectedPattern.Contains("Bearish", StringComparison.OrdinalIgnoreCase))
        {
            sellScore += 12; // Halved
            reasons.Add($"Pattern {ind.DetectedPattern} ✅SELL");
        }
        else
        {
            reasons.Add($"Pattern: {ind.DetectedPattern} (Neutral)");
        }
    }
    #pragma warning restore CS0162

    /// <summary>
    /// ADX(14) + DI+/DI- trend-strength scorer. Only contributes in trending/volatile regimes
    /// where directional movement is real; in ranging markets ADX is near zero and directional
    /// reads are noise. Complements EMA alignment (price-level) with momentum-level signal.
    /// </summary>
    private static void EvaluateAdx(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.ADX is null || ind.DIPlus is null || ind.DIMinus is null) return;

        double adx = ind.ADX.Value;
        double diPlus = ind.DIPlus.Value;
        double diMinus = ind.DIMinus.Value;

        // Ranging regime: ADX typically < 20 and direction flip-flops. Log only.
        if (regime == MarketRegime.Ranging)
        {
            reasons.Add($"ADX={adx:F1} (Range regime, directional read suppressed)");
            return;
        }

        int points;
        if (adx >= 30) points = 18;       // Strong trend
        else if (adx >= 22) points = 10;  // Moderate trend
        else { reasons.Add($"ADX={adx:F1} (weak trend, skip)"); return; }

        if (diPlus > diMinus)
        {
            buyScore += points;
            reasons.Add($"ADX={adx:F1} DI+={diPlus:F1}>DI-={diMinus:F1} (trend up ✅BUY +{points})");
        }
        else if (diMinus > diPlus)
        {
            sellScore += points;
            reasons.Add($"ADX={adx:F1} DI-={diMinus:F1}>DI+={diPlus:F1} (trend down ✅SELL +{points})");
        }
    }

    /// <summary>
    /// Rate-of-Change(10) momentum scorer. Non-lagging alternative to MACD histogram.
    /// Threshold tuned per regime: volatile markets need higher thresholds to filter noise.
    /// </summary>
    private static void EvaluateRoc(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.ROC is null) return;

        double roc = ind.ROC.Value;
        double strong = regime == MarketRegime.Volatile ? 2.0 : 1.0;
        double weak   = regime == MarketRegime.Volatile ? 1.0 : 0.3;

        if (roc >= strong)
        {
            buyScore += 12;
            reasons.Add($"ROC={roc:F2}% (strong bullish momentum ✅BUY +12)");
        }
        else if (roc >= weak)
        {
            buyScore += 6;
            reasons.Add($"ROC={roc:F2}% (bullish momentum ✅BUY +6)");
        }
        else if (roc <= -strong)
        {
            sellScore += 12;
            reasons.Add($"ROC={roc:F2}% (strong bearish momentum ✅SELL +12)");
        }
        else if (roc <= -weak)
        {
            sellScore += 6;
            reasons.Add($"ROC={roc:F2}% (bearish momentum ✅SELL +6)");
        }
        else
        {
            reasons.Add($"ROC={roc:F2}% (neutral)");
        }
    }

    /// <summary>
    /// StochRSI(14,14,3,3) mean-reversion pivots. Weighted higher in Ranging (oscillator edge)
    /// and lower in Trending (would fight the trend). K/D crossover adds a pivot-direction bonus.
    /// </summary>
    private static void EvaluateStochRsi(IndicatorSnapshot ind, MarketRegime regime, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if (ind.StochRsiK is null) return;

        double k = ind.StochRsiK.Value;
        double? d = ind.StochRsiD;

        int extremePoints = regime switch
        {
            MarketRegime.Ranging => 18,
            MarketRegime.Trending => 6,
            _ => 10
        };

        if (k <= 20)
        {
            buyScore += extremePoints;
            reasons.Add($"StochRSI K={k:F1} (oversold ✅BUY +{extremePoints})");
        }
        else if (k >= 80)
        {
            sellScore += extremePoints;
            reasons.Add($"StochRSI K={k:F1} (overbought ✅SELL +{extremePoints})");
        }

        if (d.HasValue)
        {
            if (k > d.Value && k < 50)
            {
                buyScore += 4;
                reasons.Add($"StochRSI K>D bullish crossover (+4)");
            }
            else if (k < d.Value && k > 50)
            {
                sellScore += 4;
                reasons.Add($"StochRSI K<D bearish crossover (+4)");
            }
        }
    }

    private static void ApplyRegimeConfluenceAdjustments(
        IndicatorSnapshot ind,
        MarketRegimeDetector.RegimeResult regime,
        ref int buyScore,
        ref int sellScore,
        List<string> reasons)
    {
        switch (regime.Regime)
        {
            case MarketRegime.Trending:
                ApplyTrendingBonus(ind, ref buyScore, ref sellScore, reasons);
                break;
            case MarketRegime.Ranging:
                ApplyRangingPenalty(ind, ref buyScore, ref sellScore, reasons);
                break;
            case MarketRegime.Volatile:
                ApplyVolatilityPenalty(ind, ref buyScore, ref sellScore, reasons);
                break;
        }
    }

    private static void ApplyTrendingBonus(IndicatorSnapshot ind, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        // 2026-04-24: Removed MACD from the condition (IC=-0.20 anti-predictive).
        // EMA alignment alone is sufficient as the trend confirmation gate.
        if (ind.EMA20 > ind.EMA50)
        {
            buyScore += 8;
            reasons.Add("Trend regime bonus: EMA uptrend aligned (+8)");
        }
        else if (ind.EMA20 < ind.EMA50)
        {
            sellScore += 8;
            reasons.Add("Trend regime bonus: EMA downtrend aligned (+8)");
        }
    }

    private static void ApplyRangingPenalty(IndicatorSnapshot ind, ref int buyScore, ref int sellScore, List<string> reasons)
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

    private static void ApplyVolatilityPenalty(IndicatorSnapshot ind, ref int buyScore, ref int sellScore, List<string> reasons)
    {
        if ((ind.VolumeRatio ?? 0) < 1.25)
        {
            buyScore -= 5;
            sellScore -= 5;
            reasons.Add("Volatility regime penalty: breakout lacks strong volume");
        }
        else
        {
            reasons.Add("Volatility regime filter passed: elevated volume present");
        }
    }
}
