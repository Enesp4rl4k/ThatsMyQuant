using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Analytics;

/// <summary>
/// Per-feature score signed by the decision direction. Positive = feature
/// supported the chosen side; negative = feature voted against it; zero = quiet.
/// Feature names mirror <c>EvaluateConfluence</c> in QuantIndicatorService.
/// </summary>
internal readonly record struct FeatureBreakdown(
    int Rsi, int Macd, int Ema, int Bollinger, int Volume,
    int AlphaTrend, int PMax, int Sr, int Pattern, int Regime);

/// <summary>
/// Alpha-predictiveness audit: replays the signal decision chain over historical
/// candles WITHOUT taking positions, records forward returns at multiple horizons,
/// and reports Information Coefficient, signal-alone cumulative PnL, and
/// conditional performance by strategy/regime/confidence bucket.
///
/// Purpose: answer "does our signal actually predict forward returns, or has
/// our backtest performance come from execution/sizing/risk-management rather
/// than genuine alpha?" — the single most important question we've never asked.
///
/// Mirrors the decision chain used by <see cref="BacktestEngineApp.TryEntry"/>
/// up to and including the router, but deliberately skips the ML inference
/// overlay (it only shifts confidence by ±4 points and its feature agent is
/// O(n²) to rebuild per candle). Skipping it makes the audit an honest
/// measurement of the rule-based + router signal.
/// </summary>
public sealed class SignalQualityAuditService
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IStrategyRouter[] _strategyRouters;
    private readonly MarketExecutionProfileService _executionProfiles;
    private readonly ILogger<SignalQualityAuditService>? _logger;

    // Horizons in bars — match the public contract of SignalAuditRecord.
    private static readonly int[] Horizons = { 1, 5, 10, 20, 50 };
    private const int MaxHorizon = 50;

    // Mirror BacktestEngine's warmup so indicator maturity matches the
    // real signal pipeline. If this drifts from the engine, the audit
    // would misrepresent what the live system sees.
    private const int WarmupBars = 60;

    public SignalQualityAuditService(
        ITechnicalAnalysisService technicalAnalysis,
        IEnumerable<IStrategyRouter> strategyRouters,
        MarketExecutionProfileService? executionProfiles = null,
        ILogger<SignalQualityAuditService>? logger = null)
    {
        _technicalAnalysis = technicalAnalysis;
        _strategyRouters = strategyRouters.ToArray();
        _executionProfiles = executionProfiles ?? new MarketExecutionProfileService();
        _logger = logger;
    }

    /// <summary>
    /// Walk candles and emit a per-candle audit record. Returns all records
    /// (including non-trade candles) so callers can filter / slice externally.
    /// </summary>
    public async Task<IReadOnlyList<SignalAuditRecord>> CollectAsync(
        string symbol,
        IReadOnlyList<Candle> candles,
        CancellationToken ct = default)
    {
        if (candles.Count <= WarmupBars + MaxHorizon)
        {
            _logger?.LogWarning("Signal audit needs at least {Min} candles, got {N}.",
                WarmupBars + MaxHorizon + 1, candles.Count);
            return Array.Empty<SignalAuditRecord>();
        }

        var records = new List<SignalAuditRecord>(candles.Count - WarmupBars - MaxHorizon);
        var risk = RiskParameters.Default;

        // Stop MaxHorizon bars before the end so fwd50 is always valid. This
        // preserves statistical honesty — every record has the same horizon
        // availability, no boundary bias.
        for (int i = WarmupBars; i < candles.Count - MaxHorizon; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Slice a window ending at candle i, matching the backtest's
            // contract (the latest candle is the decision candle).
            var window = new ArraySegment<Candle>(candles as Candle[] ?? candles.ToArray(), 0, i + 1);
            var candle = candles[i];

            var signal = new Signal { Symbol = symbol, Price = candle.Close };
            var regimeResult = MarketRegimeDetector.Detect(window);

            TradeDecision decision;
            try
            {
                decision = await _technicalAnalysis.AnalyzeAsync(signal, window, risk).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "TA analyze failed at {Timestamp}", candle.Timestamp);
                continue;
            }
            if (decision is null) continue;

            decision = ApplyStrategyRouter(signal, decision, regimeResult, candle);

            // Forward returns, signed by decision direction.
            decimal entry = candle.Close;
            var fwd1  = SignedReturn(entry, candles[i + 1].Close,  decision.Side);
            var fwd5  = SignedReturn(entry, candles[i + 5].Close,  decision.Side);
            var fwd10 = SignedReturn(entry, candles[i + 10].Close, decision.Side);
            var fwd20 = SignedReturn(entry, candles[i + 20].Close, decision.Side);
            var fwd50 = SignedReturn(entry, candles[i + 50].Close, decision.Side);

            Dictionary<string, int> featureScores = new();
            if (decision.ShouldTrade && decision.Indicators is not null)
            {
                var fb = ScoreFeatures(decision.Indicators, regimeResult, decision.Side);
                featureScores = new Dictionary<string, int>
                {
                    ["RSI"]        = fb.Rsi,
                    ["MACD"]       = fb.Macd,
                    ["EMA"]        = fb.Ema,
                    ["Bollinger"]  = fb.Bollinger,
                    ["Volume"]     = fb.Volume,
                    ["AlphaTrend"] = fb.AlphaTrend,
                    ["PMax"]       = fb.PMax,
                    ["SR"]         = fb.Sr,
                    ["Pattern"]    = fb.Pattern,
                    ["Regime"]     = fb.Regime,
                };
            }

            records.Add(new SignalAuditRecord
            {
                Timestamp   = candle.Timestamp,
                Symbol      = symbol,
                ShouldTrade = decision.ShouldTrade,
                Side        = decision.Side,
                Confidence  = decision.Confidence,
                StrategyTag = decision.StrategyTag,
                RegimeTag   = decision.RegimeTag,
                EntryPrice  = entry,
                Fwd1        = fwd1,
                Fwd5        = fwd5,
                Fwd10       = fwd10,
                Fwd20       = fwd20,
                Fwd50       = fwd50,
                FeatureScores = featureScores,
            });
        }

        return records;
    }

    /// <summary>
    /// Aggregate audit records into a report. Only ShouldTrade==true records
    /// are counted as "candidate signals" — the pre-confidence-gate set that
    /// actually carries predictive content.
    /// </summary>
    public SignalQualityReport BuildReport(
        string symbol,
        string timeframe,
        DateTime periodStart,
        DateTime periodEnd,
        int candlesEvaluated,
        IReadOnlyList<SignalAuditRecord> records)
    {
        var candidates = records.Where(r => r.ShouldTrade).ToList();

        var overall = ComputeStats(candidates);
        var ic = ComputeIc(candidates);

        var byStrategy = candidates
            .GroupBy(r => r.StrategyTag)
            .ToDictionary(g => g.Key, g => ComputeStats(g.ToList()));
        var byRegime = candidates
            .GroupBy(r => r.RegimeTag)
            .ToDictionary(g => g.Key, g => ComputeStats(g.ToList()));
        var byBucket = candidates
            .GroupBy(r => ConfidenceBucket(r.Confidence))
            .ToDictionary(g => g.Key, g => ComputeStats(g.ToList()));

        decimal cum10 = candidates.Sum(r => r.Fwd10);
        decimal cum20 = candidates.Sum(r => r.Fwd20);

        var byFeatureIc = ComputeFeatureIcs(candidates);

        return new SignalQualityReport
        {
            Symbol = symbol,
            Timeframe = timeframe,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            CandlesEvaluated = candlesEvaluated,
            Candidates = candidates.Count,
            Overall = overall,
            IC = ic,
            ByStrategy = byStrategy,
            ByRegime = byRegime,
            ByConfidenceBucket = byBucket,
            SignalAloneCumulativeReturnPercent = cum10,
            SignalAloneCumulativeReturn20Percent = cum20,
            ByFeatureIc = byFeatureIc,
        };
    }

    /// <summary>
    /// For each feature family, compute Spearman IC between the feature's
    /// signed contribution and forward returns. Also split the candidate set
    /// by whether the feature supported the decision (net>0) or was silent/against
    /// (net<=0) and report the mean fwd10 in each bucket. The gap between
    /// buckets is the cleanest evidence of whether that feature actually adds
    /// alpha.
    /// </summary>
    private static IReadOnlyDictionary<string, FeatureIc> ComputeFeatureIcs(IReadOnlyList<SignalAuditRecord> candidates)
    {
        var result = new Dictionary<string, FeatureIc>();
        var features = new[] { "RSI", "MACD", "EMA", "Bollinger", "Volume", "AlphaTrend", "PMax", "SR", "Pattern", "Regime" };
        foreach (var f in features)
        {
            var rows = candidates
                .Where(r => r.FeatureScores.ContainsKey(f))
                .Select(r => (Score: r.FeatureScores[f], Fwd10: (double)r.Fwd10, Fwd20: (double)r.Fwd20))
                .ToList();
            if (rows.Count < 10)
            {
                result[f] = new FeatureIc { N = rows.Count, IcFwd10 = 0, IcFwd20 = 0, MeanFwd10WhenPositive = 0, MeanFwd10WhenNonPositive = 0 };
                continue;
            }
            var scores = rows.Select(r => (double)r.Score).ToArray();
            var fwd10 = rows.Select(r => r.Fwd10).ToArray();
            var fwd20 = rows.Select(r => r.Fwd20).ToArray();

            var pos = rows.Where(r => r.Score > 0).ToList();
            var nonPos = rows.Where(r => r.Score <= 0).ToList();
            decimal posMean = pos.Count > 0 ? (decimal)pos.Average(r => r.Fwd10) : 0m;
            decimal nonPosMean = nonPos.Count > 0 ? (decimal)nonPos.Average(r => r.Fwd10) : 0m;

            result[f] = new FeatureIc
            {
                N = rows.Count,
                IcFwd10 = SpearmanCorrelation(scores, fwd10),
                IcFwd20 = SpearmanCorrelation(scores, fwd20),
                MeanFwd10WhenPositive = posMean,
                MeanFwd10WhenNonPositive = nonPosMean,
            };
        }
        return result;
    }

    // --- per-feature score replica (must stay in sync with
    // QuantIndicatorService.EvaluateConfluence in
    // 03_Infrastructure/Data.Persistence/Services/QuantIndicatorService.cs).
    // Returned values are signed by the decision direction:
    //     +N = feature voted for the chosen side
    //     -N = feature voted against it
    //      0 = feature silent / partial / neutral
    // ---------------------------------------------------------------------
    private static FeatureBreakdown ScoreFeatures(IndicatorSnapshot ind, MarketRegimeDetector.RegimeResult regime, TradeSide side)
    {
        int rsiBuy = 0, rsiSell = 0;
        if (ind.RSI.HasValue)
        {
            if (ind.RSI < 30) rsiBuy = 30;
            else if (ind.RSI <= 50) rsiBuy = 22;
            else if (ind.RSI > 70) rsiSell = 30;
            else if (ind.RSI >= 50) rsiSell = 15;
        }

        int macdBuy = 0, macdSell = 0;
        if (ind.MACDHistogram.HasValue)
        {
            if (ind.MACDHistogram > 0 && ind.MACD > ind.MACDSignal) macdBuy = 25;
            else if (ind.MACDHistogram < 0 && ind.MACD < ind.MACDSignal) macdSell = 25;
        }

        int emaBuy = 0, emaSell = 0;
        if (ind.EMA20.HasValue && ind.EMA50.HasValue)
        {
            double price = (double)ind.CurrentPrice;
            if (price > ind.EMA20 && ind.EMA20 > ind.EMA50) emaBuy = 0;  // zeroed per IC audit
            else if (price < ind.EMA20 && ind.EMA20 < ind.EMA50) emaSell = 0;  // zeroed per IC audit
        }

        int bbBuy = 0, bbSell = 0;
        if (ind.BollingerLower.HasValue && ind.BollingerUpper.HasValue)
        {
            double price = (double)ind.CurrentPrice;
            double width = ind.BollingerUpper.Value - ind.BollingerLower.Value;
            if (price <= ind.BollingerLower.Value + width * 0.1) bbBuy = 15;
            else if (price >= ind.BollingerUpper.Value - width * 0.1) bbSell = 15;
        }

        // Volume bonus is rubber-stamp for whichever side is leading pre-volume —
        // approximation here: award to the decision side when ratio >= 1.3.
        int volBuy = 0, volSell = 0;
        if ((ind.VolumeRatio ?? 0) >= 1.3)
        {
            if (side == TradeSide.Buy) volBuy = 20; else volSell = 20;
        }

        int atBuy = 0, atSell = 0;
        if (ind.AlphaTrend != null)
        {
            if (ind.AlphaTrend.Signal == TradeSide.Buy && ind.AlphaTrend.IsConfirmed) atBuy = 0;  // zeroed per IC audit pass 2
            else if (ind.AlphaTrend.Signal == TradeSide.Sell && ind.AlphaTrend.IsConfirmed) atSell = 0;
            else if (ind.AlphaTrend.IsBullish) atBuy = 0;
            else atSell = 0;
        }

        int pmaxBuy = 0, pmaxSell = 0;
        if (ind.PMax != null)
        {
            if (ind.PMax.Signal == TradeSide.Buy || (ind.PMax.IsBullish && ind.CurrentPrice > (decimal)ind.PMax.Value)) pmaxBuy = 15;
            else if (ind.PMax.Signal == TradeSide.Sell || (!ind.PMax.IsBullish && ind.CurrentPrice < (decimal)ind.PMax.Value)) pmaxSell = 15;
        }

        int srBuy = 0, srSell = 0;
        if (ind.NearestSupport != null)
        {
            decimal d = Math.Abs(ind.CurrentPrice - ind.NearestSupport.Price) / ind.CurrentPrice;
            if (d < 0.02m) srBuy = 20;  // was 15; IC-boosted
        }
        if (ind.NearestResistance != null)
        {
            decimal d = Math.Abs(ind.NearestResistance.Price - ind.CurrentPrice) / ind.CurrentPrice;
            if (d < 0.02m) srSell = 20;  // was 15; IC-boosted
        }

        int patBuy = 0, patSell = 0;
        if (!string.IsNullOrEmpty(ind.DetectedPattern) && ind.DetectedPattern != "None")
        {
            if (ind.DetectedPattern.Contains("Bullish", StringComparison.OrdinalIgnoreCase)) patBuy = 12;  // was 25; halved pass 2
            else if (ind.DetectedPattern.Contains("Bearish", StringComparison.OrdinalIgnoreCase)) patSell = 12;
        }

        int regBuy = 0, regSell = 0;
        switch (regime.Regime)
        {
            case MarketRegime.Trending:
                if (ind.EMA20 > ind.EMA50 && ind.MACDHistogram > 0) regBuy = 10;
                if (ind.EMA20 < ind.EMA50 && ind.MACDHistogram < 0) regSell = 10;
                break;
            case MarketRegime.Ranging:
                if (!(ind.RSI is > 40 and < 60)) { regBuy = -5; regSell = -5; }
                break;
            case MarketRegime.Volatile:
                if ((ind.VolumeRatio ?? 0) < 1.8) { regBuy = -10; regSell = -10; }
                break;
        }

        // Sign by decision side: positive = supported the chosen direction.
        int Net(int buy, int sell) => side == TradeSide.Buy ? (buy - sell) : (sell - buy);

        return new FeatureBreakdown(
            Rsi: Net(rsiBuy, rsiSell),
            Macd: Net(macdBuy, macdSell),
            Ema: Net(emaBuy, emaSell),
            Bollinger: Net(bbBuy, bbSell),
            Volume: Net(volBuy, volSell),
            AlphaTrend: Net(atBuy, atSell),
            PMax: Net(pmaxBuy, pmaxSell),
            Sr: Net(srBuy, srSell),
            Pattern: Net(patBuy, patSell),
            Regime: Net(regBuy, regSell));
    }

    // --- router replay (mirror of BacktestEngine.ApplyStrategyRouter) ---
    private TradeDecision ApplyStrategyRouter(
        Signal signal,
        TradeDecision decision,
        MarketRegimeDetector.RegimeResult regime,
        Candle candle)
    {
        if (_strategyRouters.Length == 0) return decision;

        var market = signal.MarketType == MarketType.Crypto ? "Crypto" : signal.MarketType.ToString();
        var router = _strategyRouters
            .OrderByDescending(r => r.EntryThreshold)
            .FirstOrDefault(r => r.CanHandle(market, signal, regime));
        if (router is null || decision.Confidence < router.EntryThreshold) return decision;

        var indicators = decision.Indicators ?? new IndicatorSnapshot { CurrentPrice = candle.Close };
        var atr = indicators.ATR is > 0 ? (decimal)indicators.ATR.Value : Math.Abs(candle.Close - decision.StopLoss);
        if (atr <= 0m) atr = candle.Close * 0.005m;

        var context = new StrategyRouterContext(
            signal,
            indicators,
            regime,
            _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata),
            decision.Side,
            decision.Confidence,
            "Signal audit",
            "Audit replay",
            decision.PositionSizePercent,
            candle.Close,
            atr,
            ResolveRoundTripCostPercent(signal),
            new List<string> { "Audit replay" });

        var routed = router.BuildDecision(context);
        return decision with
        {
            StrategyTag = routed.StrategyTag,
            ExitPolicyTag = routed.ExitPolicyTag,
            RegimeTag = routed.RegimeTag,
        };
    }

    // --- stats helpers ---
    private static decimal SignedReturn(decimal entry, decimal future, TradeSide side)
    {
        if (entry <= 0m) return 0m;
        var raw = (future - entry) / entry * 100m;
        return side == TradeSide.Sell ? -raw : raw;
    }

    private static string ConfidenceBucket(int c) =>
        c switch
        {
            < 60 => "<60",
            < 70 => "60-69",
            < 80 => "70-79",
            < 90 => "80-89",
            _    => "90-100",
        };

    private static SignalReturnStats ComputeStats(IReadOnlyList<SignalAuditRecord> rs)
    {
        if (rs.Count == 0)
        {
            return new SignalReturnStats
            {
                N = 0, MeanFwd1 = 0, MeanFwd5 = 0, MeanFwd10 = 0, MeanFwd20 = 0, MeanFwd50 = 0,
                MedianFwd10 = 0, HitRateFwd10 = 0, HitRateFwd20 = 0
            };
        }

        decimal Mean(Func<SignalAuditRecord, decimal> sel) => rs.Sum(sel) / rs.Count;

        var sorted10 = rs.Select(r => r.Fwd10).OrderBy(x => x).ToList();
        var median10 = sorted10[sorted10.Count / 2];

        decimal hr10 = (decimal)rs.Count(r => r.Fwd10 > 0) / rs.Count;
        decimal hr20 = (decimal)rs.Count(r => r.Fwd20 > 0) / rs.Count;

        return new SignalReturnStats
        {
            N = rs.Count,
            MeanFwd1  = Mean(r => r.Fwd1),
            MeanFwd5  = Mean(r => r.Fwd5),
            MeanFwd10 = Mean(r => r.Fwd10),
            MeanFwd20 = Mean(r => r.Fwd20),
            MeanFwd50 = Mean(r => r.Fwd50),
            MedianFwd10 = median10,
            HitRateFwd10 = hr10,
            HitRateFwd20 = hr20,
        };
    }

    private decimal ResolveRoundTripCostPercent(Signal signal)
    {
        var profile = _executionProfiles.ResolveForSymbol(signal.Symbol, signal.Metadata);
        return profile.SpreadPercent + (profile.SlippagePercent * 2m) + (profile.CommissionPercentPerSide * 2m);
    }

    /// <summary>
    /// Spearman rank correlation between confidence and forward return.
    /// Rank IC is preferred over Pearson because confidence is ordinal-ish
    /// and we care about monotonicity, not linearity.
    /// </summary>
    private static InformationCoefficient ComputeIc(IReadOnlyList<SignalAuditRecord> rs)
    {
        double Ic(Func<SignalAuditRecord, decimal> sel)
        {
            if (rs.Count < 10) return 0d;
            var conf = rs.Select(r => (double)r.Confidence).ToArray();
            var fwd = rs.Select(r => (double)sel(r)).ToArray();
            return SpearmanCorrelation(conf, fwd);
        }

        return new InformationCoefficient
        {
            Fwd1 = Ic(r => r.Fwd1),
            Fwd5 = Ic(r => r.Fwd5),
            Fwd10 = Ic(r => r.Fwd10),
            Fwd20 = Ic(r => r.Fwd20),
            Fwd50 = Ic(r => r.Fwd50),
        };
    }

    private static double SpearmanCorrelation(double[] x, double[] y)
    {
        if (x.Length != y.Length || x.Length < 2) return 0d;
        var rx = Rank(x);
        var ry = Rank(y);
        double mx = rx.Average(), my = ry.Average();
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < rx.Length; i++)
        {
            var ax = rx[i] - mx;
            var ay = ry[i] - my;
            num += ax * ay;
            dx += ax * ax;
            dy += ay * ay;
        }
        var denom = Math.Sqrt(dx * dy);
        return denom == 0 ? 0d : num / denom;
    }

    private static double[] Rank(double[] v)
    {
        var indexed = v.Select((val, i) => (val, i)).OrderBy(t => t.val).ToArray();
        var ranks = new double[v.Length];
        int n = v.Length;
        int k = 0;
        while (k < n)
        {
            int j = k;
            while (j + 1 < n && indexed[j + 1].val == indexed[k].val) j++;
            double avgRank = (k + j) / 2.0 + 1; // 1-based average rank on ties
            for (int m = k; m <= j; m++) ranks[indexed[m].i] = avgRank;
            k = j + 1;
        }
        return ranks;
    }
}
