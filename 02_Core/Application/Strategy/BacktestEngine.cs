using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

public class BacktestEngineApp
{
    private readonly ITechnicalAnalysisService _technicalAnalysis;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly TickReplayEngine? _tickReplayEngine;
    private readonly ILogger<BacktestEngineApp> _logger;
    // Mutable so RunAsync can override per-symbol from OKX live spread + fee schedule. The
    // default constructed BacktestCostModel is used only as a fallback if the profile provider
    // is absent or the venue lookup fails.
    private BacktestCostModel _costs;
    private readonly ISymbolCostProfileProvider? _symbolCostProvider;
    private readonly IFundingRateProvider? _fundingRateProvider;
    private readonly BacktestTimeframeProfileOptions _profileOptions;
    private readonly BacktestGuardOptions _guardOptions;
    private readonly BacktestPerformanceOptions _perfOptions;
    /// <summary>WFO / parallel grid: merged profile for this async flow only (never mutates IOptions snapshot).</summary>
    private readonly System.Threading.AsyncLocal<BacktestProfile?> _wfoProfileOverlay = new();
    private readonly IStrategyRouter[] _strategyRouters;
    private readonly EdgeScoreService? _edgeScoreService;
    private readonly InferenceAgent? _inferenceAgent;
    private readonly FeatureEngineeringAgent? _featureEngineeringAgent;
    private readonly IHftSignalGenerationService? _hftSignalGenerationService;
    /// <summary>
    /// Per-run mutable state isolated via <see cref="System.Threading.AsyncLocal{T}"/> so
    /// parallel PBO segment replays cannot corrupt each other's gates, coverage flags, or
    /// reject histograms (all of which previously lived on the engine instance and raced).
    /// </summary>
    private sealed class BacktestRunContext
    {
        public readonly Queue<double> ConfidenceHistory = new(512);
        public readonly Dictionary<string, int> EntryRejectCounts = new(StringComparer.OrdinalIgnoreCase);
        public DecisionCoverageFlags CoverageFlags = DecisionCoverageFlags.None;
        public bool InferencePredictionValid;
    }

    private readonly AsyncLocal<BacktestRunContext?> _runContext = new();
    private readonly AsyncLocal<BacktestExecutionSettings?> _executionSettings = new();

    private sealed class BacktestExecutionSettings
    {
        public decimal Leverage { get; set; }
        public bool HftMode { get; set; }
        public IReadOnlyList<Candle>? HtfCandles { get; set; }
        public int HtfCursor { get; set; }
        public string Timeframe { get; set; } = "1h";
        public string RiskProfile { get; set; } = "Moderate";
        public BacktestCostModel Costs { get; set; } = new();
    }

    private BacktestRunContext RequireRunContext()
        => _runContext.Value
           ?? throw new InvalidOperationException(
               "BacktestEngineApp requires an active run context (RunAsync / RunOnSegmentsAsync).");

    private BacktestExecutionSettings CreateExecutionSettingsSnapshot()
        => new()
        {
            Leverage = 1m,
            HftMode = false,
            HtfCandles = _htfCandles,
            HtfCursor = _htfCursor,
            Timeframe = "1h",
            RiskProfile = "Moderate",
            Costs = CloneCosts(_costs)
        };

    private BacktestExecutionSettings CurrentExecutionSettings
        => _executionSettings.Value ?? CreateExecutionSettingsSnapshot();

    private decimal CurrentLeverage => CurrentExecutionSettings.Leverage;
    private bool CurrentHftMode => CurrentExecutionSettings.HftMode;
    private IReadOnlyList<Candle>? CurrentHtfCandles => CurrentExecutionSettings.HtfCandles;
    private int CurrentHtfCursor
    {
        get => CurrentExecutionSettings.HtfCursor;
        set
        {
            if (_executionSettings.Value is not null)
            {
                _executionSettings.Value.HtfCursor = value;
                return;
            }

            _htfCursor = value;
        }
    }
    // Always normalize — callers have been seen to pass "1H"/"4H" from config and the
    // switch expressions below are case-sensitive. One source of truth here guarantees
    // "1h" / "4h" / "15m" regardless of caller casing.
    private string CurrentTimeframe =>
        (CurrentExecutionSettings.Timeframe ?? string.Empty).Trim().ToLowerInvariant();
    private string CurrentRiskProfile => CurrentExecutionSettings.RiskProfile;
    private BacktestCostModel CurrentCosts => CurrentExecutionSettings.Costs;

    private void SetCurrentCosts(BacktestCostModel costs)
    {
        if (_executionSettings.Value is not null)
        {
            _executionSettings.Value.Costs = costs;
            return;
        }

        _costs = costs;
    }

    private async Task<BacktestResult> RunWithScopedExecutionSettingsAsync(
        Func<BacktestExecutionSettings, Task<BacktestResult>> run,
        Action<BacktestExecutionSettings>? configure = null)
    {
        var prior = _executionSettings.Value;
        var scoped = CreateExecutionSettingsSnapshot();
        configure?.Invoke(scoped);
        _executionSettings.Value = scoped;
        try
        {
            return await run(scoped).ConfigureAwait(false);
        }
        finally
        {
            _executionSettings.Value = prior;
        }
    }

    private Queue<double> GetConfidenceHistory() => RequireRunContext().ConfidenceHistory;
    private const int ConfidenceHistoryWindow = 500;
    /// <summary>Replay warm-up bars fed to indicators before signal generation begins.
    /// Kept at 60 — tested at 120 twice: both times activated VolatilityFloor/VolumeFloor gates
    /// that systematically rejected higher-quality trades. See comment on RunAsync for full history.</summary>
    private const int WarmupBars = 60;
    /// <summary>Default number of segments for Probability of Backtest Overfitting (PBO) analysis.</summary>
    private const int PboDefaultSegments = 16;
    // Minimum samples before the rolling percentile gate activates. Now timeframe-dependent:
    // higher TFs have fewer bars per session, so the activation threshold scales down.
    // Formula: max(60, barsPerDay × 10). 4h ≈ 60, 1h ≈ 240, 15m ≈ 960.
    // This fixes the MinTrades<30 blocker on short horizons (90d × 4h = 540 bars total,
    // a 200-sample activation meant the gate barely engaged).
    private int ResolveConfidenceHistoryMinActivation() => CurrentTimeframe switch
    {
        "1m" => 500,
        "5m" => 400,
        "15m" => 300,
        "1h" => 200,
        "4h" => 60,
        "1d" => 30,
        _ => 200
    };
    // 2026-04-18: tested asymmetric-sizing shrinkage (size *= 80/conf when conf>80).
    // Hypothesis was that high-confidence losers scale faster than winners (per the cap-raise
    // regression). Falsified: winners shrunk 5%, losers shrunk 4% — symmetric, no confidence
    // asymmetry. Sharpe regressed 0.59→0.53 and PBO exploded 0.365→0.706 (grid-search
    // amplification, same pattern as the maker-offset test). Keeping this comment as a marker
    // so the lever doesn't get re-tried without new evidence of actual asymmetry.
    // private const double ConfidenceSizeCeiling = 80.0;
    // Tested 0.55 on 2026-04-18 under real OKX costs: BTC 4h WR dropped 45.3% → 41.8%,
    // SOL 1h collapsed to 26% WR / Sharpe -5. The rolling-percentile gate is genuinely
    // doing work — marginal signals below its threshold are net-negative expectancy, not
    // merely "marginal cost-wise." Reverting to 0.70.
    // 2026-04-18: tried 0.75 (top 25%) and it was a no-op — trade count/WR/pooled-Sharpe all
    // identical to 0.70. Interpretation: confidence distribution is bimodal (passes strongly
    // above threshold, rejects strongly below), so slider moves in the 0.60–0.80 band are
    // inert.
    // 2026-04-24: retested 0.55 with EnsembleVote relaxed concurrently — WR collapsed 57%→21%
    // (+23 extra trades were net-negative expectancy). The bimodal-split interpretation holds:
    // below ~0.65 we dip into the low-quality peak. Reverted to 0.70. Do not re-test without
    // new evidence that confidence calibration has changed (e.g. after model retraining).
    private const double ConfidencePercentileFloor = 0.70;

    // Higher-timeframe candles for real MTF confluence gate (e.g. 4h series on a 1h session).
    // Populated by orchestrator via SetHigherTimeframeCandles before RunAsync.
    private IReadOnlyList<Candle>? _htfCandles;
    // Index cursor for efficient HTF lookups — HTF candles are time-ordered so we just advance.
    private int _htfCursor;

    public void SetHigherTimeframeCandles(IReadOnlyList<Candle>? candles)
    {
        _htfCandles = candles;
        _htfCursor = 0;
    }
    // Maintenance margin rate for liquidation proxy. OKX perp tier-1 ~0.5%.
    private const decimal MaintenanceMarginRate = 0.005m;

    public BacktestEngineApp(
        ITechnicalAnalysisService technicalAnalysis,
        IAgentCoordinator agentCoordinator,
        ILogger<BacktestEngineApp> logger,
        TickReplayEngine? tickReplayEngine = null,
        BacktestCostModel? costs = null,
        IOptions<BacktestTimeframeProfileOptions>? profileOptions = null,
        IOptions<BacktestGuardOptions>? guardOptions = null,
        IOptions<BacktestPerformanceOptions>? performanceOptions = null,
        IEnumerable<IStrategyRouter>? strategyRouters = null,
        EdgeScoreService? edgeScoreService = null,
        InferenceAgent? inferenceAgent = null,
        FeatureEngineeringAgent? featureEngineeringAgent = null,
        IHftSignalGenerationService? hftSignalGenerationService = null,
        RegimeExpectancyService? regimeExpectancy = null,
        FundingRateSignalService? fundingRateSignal = null,
        ISymbolCostProfileProvider? symbolCostProvider = null,
        IFundingRateProvider? fundingRateProvider = null)
    {
        _technicalAnalysis = technicalAnalysis ?? throw new ArgumentNullException(nameof(technicalAnalysis));
        _agentCoordinator = agentCoordinator ?? throw new ArgumentNullException(nameof(agentCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tickReplayEngine = tickReplayEngine;
        _costs = costs ?? new BacktestCostModel();
        _profileOptions = profileOptions?.Value ?? new BacktestTimeframeProfileOptions();
        _guardOptions = guardOptions?.Value ?? new BacktestGuardOptions();
        _perfOptions = performanceOptions?.Value ?? new BacktestPerformanceOptions();
        _strategyRouters = strategyRouters?.ToArray() ?? Array.Empty<IStrategyRouter>();
        _edgeScoreService = edgeScoreService;
        _inferenceAgent = inferenceAgent;
        _featureEngineeringAgent = featureEngineeringAgent;
        _hftSignalGenerationService = hftSignalGenerationService;
        _regimeExpectancy = regimeExpectancy;
        _fundingRateSignal = fundingRateSignal;
        _symbolCostProvider = symbolCostProvider;
        _fundingRateProvider = fundingRateProvider;
    }

    /// <summary>
    /// Apply per-symbol real cost overrides from the venue (OKX). Called at the top of every
    /// RunAsync entry point. If the provider is absent or the lookup fails, the in-effect
    /// <see cref="_costs"/> is unchanged (defaults or ctor-supplied model). Warmed funding is
    /// cached by the funding provider; the background prefetch also runs for the date range.
    /// </summary>
    private async Task ApplyRealCostProfileAsync(string symbol, DateTime fromUtc, CancellationToken ct)
    {
        // Funding prefetch (safe no-op for NullFundingRateProvider). Ensures GetRate hot-loop
        // hits are pure in-memory thereafter.
        if (_fundingRateProvider is not null)
        {
            try { await _fundingRateProvider.PrefetchAsync(new[] { symbol }, fromUtc, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Funding prefetch failed for {Symbol}", symbol); }
        }

        if (_symbolCostProvider is null) return;
        try
        {
            var profile = await _symbolCostProvider.GetAsync(symbol, ct).ConfigureAwait(false);
            if (profile is null) return;
            var p = profile.Value;
            var currentCosts = CurrentCosts;

            SetCurrentCosts(new BacktestCostModel
            {
                CommissionBps = p.TakerBps,
                MakerCommissionBps = p.MakerBps,
                SpreadBps = p.SpreadBps,
                SlippageBps = currentCosts.SlippageBps,
                AtrSlippageMultiplier = currentCosts.AtrSlippageMultiplier,
                MaxSlippageFractionOfBarRange = currentCosts.MaxSlippageFractionOfBarRange,
                SizeImpactBpsPerNotionalPct = currentCosts.SizeImpactBpsPerNotionalPct,
                VolatileSpreadMultiplier = currentCosts.VolatileSpreadMultiplier,
                FundingBpsPer8h = currentCosts.FundingBpsPer8h,  // unused when provider is non-null; preserved for legacy-path safety
                FundingVolatilityMultiplier = currentCosts.FundingVolatilityMultiplier,
                UseMakerEntries = currentCosts.UseMakerEntries,
                MakerEntryOffsetBps = currentCosts.MakerEntryOffsetBps,
                MakerEntryTimeoutBars = currentCosts.MakerEntryTimeoutBars
            });
            _logger.LogInformation(
                "Backtest costs overridden from venue for {Symbol}: taker={Taker}bps maker={Maker}bps spread={Spread:F2}bps",
                symbol, p.TakerBps, p.MakerBps, p.SpreadBps);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cost profile lookup failed for {Symbol}; using defaults.", symbol);
        }
    }

    private readonly RegimeExpectancyService? _regimeExpectancy;
    private readonly FundingRateSignalService? _fundingRateSignal;

    public Task<BacktestResult> RunAsync(List<Candle> historicalCandles, string symbol, decimal startingCapital, decimal leverage)
        => RunWithScopedExecutionSettingsAsync(
            _ => RunAsync(historicalCandles, symbol, startingCapital),
            settings => settings.Leverage = leverage > 0m ? leverage : 1m);

    public Task<BacktestResult> RunHftAsync(List<Candle> historicalCandles, string symbol, string timeframe, decimal startingCapital = 100_000m)
        => RunWithScopedExecutionSettingsAsync(
            _ => RunAsync(historicalCandles, symbol, startingCapital),
            settings =>
            {
                settings.HftMode = true;
                settings.Timeframe = NormalizeTimeframe(timeframe);
            });

    public Task<BacktestResult> RunAsync(List<Candle> historicalCandles, string symbol, string timeframe, decimal startingCapital = 100_000m)
        => RunWithScopedExecutionSettingsAsync(
            _ => RunAsync(historicalCandles, symbol, startingCapital),
            settings => settings.Timeframe = NormalizeTimeframe(timeframe));

    public Task<BacktestResult> RunAsync(
        List<Candle> historicalCandles,
        string symbol,
        string timeframe,
        string riskProfile,
        decimal startingCapital = 100_000m)
        => RunWithScopedExecutionSettingsAsync(
            _ => RunAsync(historicalCandles, symbol, startingCapital),
            settings =>
            {
                settings.Timeframe = NormalizeTimeframe(timeframe);
                settings.RiskProfile = NormalizeRiskProfile(riskProfile);
            });

    public async Task<BacktestResult> RunAsync(List<Candle> historicalCandles, string symbol, decimal startingCapital = 100_000m)
    {
        if (_executionSettings.Value is null)
        {
            return await RunWithScopedExecutionSettingsAsync(
                _ => RunAsync(historicalCandles, symbol, startingCapital)).ConfigureAwait(false);
        }

        _executionSettings.Value!.Timeframe = NormalizeTimeframe(_executionSettings.Value.Timeframe);
        _executionSettings.Value.RiskProfile = NormalizeRiskProfile(_executionSettings.Value.RiskProfile);
        if (historicalCandles == null || historicalCandles.Count < WarmupBars)
        {
            _logger.LogWarning("Insufficient data for {Symbol}", symbol);
            return new BacktestResult { Symbol = symbol };
        }

            // Real-cost overlay: fetch per-symbol spread + OKX fee schedule and warm the funding
            // history cache for the run's date range. No-op if providers are null (unit tests) or
            // the venue lookup fails (offline). Must run BEFORE the replay loop uses _costs.
            var dataFromUtc = historicalCandles[0].Timestamp;
            await ApplyRealCostProfileAsync(symbol, dataFromUtc, CancellationToken.None).ConfigureAwait(false);

            var runCtx = new BacktestRunContext();
            var prevCtx = _runContext.Value;
            _runContext.Value = runCtx;
            try
            {
            var state = new BacktestState(startingCapital);
            var trades = new List<BacktestTradeResult>();
            var fullTradeHistory = new List<Trade>();
            PaperPosition? openPosition = null;

            // 🔥 HFT Optimization: Use a shared array and ArraySegment for zero-copy sliding windows
            var candlesArray = historicalCandles.ToArray();

            int total = historicalCandles.Count;
            int barsToReplay = total - 60;
            int progressSteps = Math.Clamp(_perfOptions.ReplayProgressSteps, 2, 10_000);
            int reportEvery = Math.Max(1, barsToReplay / progressSteps);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (_perfOptions.LogReplayProgressToConsole)
            {
                Console.WriteLine($"[Backtest] Replaying {barsToReplay} candles for {symbol}...");
            }

            // Replay warmup — value governed by class-level WarmupBars const.
            // See its XML doc for the full A/B test history (tested 120 twice, reverted both times).
            for (int i = WarmupBars; i < historicalCandles.Count; i++)
            {
                var candle = candlesArray[i];
                state.AdvanceBar(candle.Timestamp);

                if (openPosition != null)
                {
                    openPosition = ProcessExit(candle, openPosition, state, trades, fullTradeHistory);
                }
                else
                {
                    var window = new ArraySegment<Candle>(candlesArray, i - WarmupBars, WarmupBars + 1);
                    // Look-ahead slice for maker-fill peek. Bounded by MakerEntryTimeoutBars to
                    // avoid unrealistic long-horizon fill claims.
                    int futureCount = Math.Min(CurrentCosts.MakerEntryTimeoutBars, historicalCandles.Count - i - 1);
                    var future = futureCount > 0
                        ? new ArraySegment<Candle>(candlesArray, i + 1, futureCount)
                        : (IReadOnlyList<Candle>?)null;
                    openPosition = await TryEntry(symbol, window, state, fullTradeHistory, future).ConfigureAwait(false);
                }

                int processed = i - WarmupBars + 1;
                if (_perfOptions.LogReplayProgressToConsole
                    && (processed % reportEvery == 0 || i == total - 1))
                {
                    double pct = 100.0 * processed / barsToReplay;
                    Console.WriteLine($"[Backtest] {processed}/{barsToReplay} ({pct:F0}%)  elapsed={sw.Elapsed:mm\\:ss}  trades={trades.Count}");
                }
            }

            ForceCloseOpenPositionIfNeeded(candlesArray, openPosition, state, trades, fullTradeHistory);
            sw.Stop();
            if (_perfOptions.LogReplayProgressToConsole)
            {
                Console.WriteLine($"[Backtest] main replay done in {sw.Elapsed:mm\\:ss}  total trades={trades.Count}");
            }
            return BuildFinalResult(
                symbol,
                historicalCandles,
                trades,
                state,
                startingCapital,
                runCtx.CoverageFlags,
                _inferenceAgent?.IsModelAvailable ?? false,
                runCtx.InferencePredictionValid,
                runCtx.EntryRejectCounts);
            }
            finally
            {
                _runContext.Value = prevCtx;
            }
    }

    private static BacktestCostModel CloneCosts(BacktestCostModel source)
        => new()
        {
            CommissionBps = source.CommissionBps,
            SpreadBps = source.SpreadBps,
            VolatileSpreadMultiplier = source.VolatileSpreadMultiplier,
            SlippageBps = source.SlippageBps,
            AtrSlippageMultiplier = source.AtrSlippageMultiplier,
            MaxSlippageFractionOfBarRange = source.MaxSlippageFractionOfBarRange,
            SizeImpactBpsPerNotionalPct = source.SizeImpactBpsPerNotionalPct,
            FundingBpsPer8h = source.FundingBpsPer8h,
            FundingVolatilityMultiplier = source.FundingVolatilityMultiplier,
            MakerCommissionBps = source.MakerCommissionBps,
            UseMakerEntries = source.UseMakerEntries,
            MakerEntryOffsetBps = source.MakerEntryOffsetBps,
            MakerEntryTimeoutBars = source.MakerEntryTimeoutBars
        };

    /// <summary>
    /// Runs a replay using a merged copy of the configured timeframe profile and the given WFO parameter triple.
    /// Safe under parallel WFO grid search (AsyncLocal isolation; does not mutate bound profile options).
    /// </summary>
    public async Task<BacktestResult> RunAsyncWithProfileOverlay(
        List<Candle> historicalCandles,
        string symbol,
        string timeframe,
        decimal startingCapital,
        ParamSet profileOverlay)
    {
        var merged = MergeWfoOverlay(ResolveOptionsProfileForTimeframe(), profileOverlay);
        var prevOverlay = _wfoProfileOverlay.Value;
        _wfoProfileOverlay.Value = merged;
        try
        {
            return await RunWithScopedExecutionSettingsAsync(
                _ => RunAsync(historicalCandles, symbol, startingCapital),
                settings => settings.Timeframe = NormalizeTimeframe(timeframe)).ConfigureAwait(false);
        }
        finally
        {
            _wfoProfileOverlay.Value = prevOverlay;
        }
    }

    public async Task<BacktestResult> RunTickReplayAsync(
        List<MarketTick> ticks,
        List<Candle> baseCandles,
        string symbol,
        decimal capital = 100_000m,
        List<OrderBookSnapshot>? orderBooks = null)
    {
        if (_tickReplayEngine == null)
        {
            _logger.LogWarning("TickReplayEngine not available. Register it in DI to enable L2 backtesting.");
            return new BacktestResult { Symbol = symbol, Reasoning = "TickReplayEngine not configured" };
        }

        _logger.LogInformation("Delegating to L2 TickReplayEngine for {Symbol}...", symbol);
        return await _tickReplayEngine.ReplayAsync(ticks, baseCandles, symbol, capital, orderBooks);
    }

    public async Task<BacktestResult> RunTickReplayFromCsvAsync(
        string ticksCsvPath,
        string orderBooksCsvPath,
        List<Candle> baseCandles,
        string symbol,
        decimal capital = 100_000m,
        CancellationToken ct = default)
    {
        if (_tickReplayEngine == null)
        {
            _logger.LogWarning("TickReplayEngine not available. Register it in DI to enable L2 backtesting.");
            return new BacktestResult { Symbol = symbol, Reasoning = "TickReplayEngine not configured" };
        }

        _logger.LogInformation("Delegating CSV L2 replay to TickReplayEngine for {Symbol}...", symbol);
        return await _tickReplayEngine.ReplayFromCsvAsync(ticksCsvPath, orderBooksCsvPath, baseCandles, symbol, capital, ct);
    }

    public async Task<PboResult> ComputePboAsync(
        List<Candle> historicalCandles,
        string symbol,
        int numSegments = PboDefaultSegments,
        decimal startingCapital = 100_000m,
        PboPartitionMode partitionMode = PboPartitionMode.Contiguous)
    {
        if (historicalCandles == null || historicalCandles.Count < numSegments * WarmupBars)
        {
            _logger.LogWarning("Insufficient data for PBO computation on {Symbol}", symbol);
            return new PboResult { PBO = 1.0 };
        }

        var segments = PartitionIntoSegments(historicalCandles, numSegments, partitionMode);
        int half = numSegments / 2;
        int totalCombinations = BinomialCoefficient(numSegments, half);

        _logger.LogInformation("Computing PBO for {Symbol} with {NumSegments} segments, {TotalCombinations} combinations...",
            symbol, numSegments, totalCombinations);

        var trainSharpeList = new List<double>();
        var testSharpeList = new List<double>();
        var logitWeights = new List<double>();
        int overfittingCount = 0;

        var pboSw = System.Diagnostics.Stopwatch.StartNew();
        int pboDone = 0;
        int pboReportEvery = Math.Max(1, totalCombinations / 10);
        if (_perfOptions.LogPboProgressToConsole)
        {
            Console.WriteLine($"[PBO] {totalCombinations} combinations to process (parallel)...");
        }

        // Materialize combinations so we can dispatch in parallel. Each iteration is
        // embarrassingly parallel (two independent RunOnSegmentsAsync calls), so running
        // them concurrently cuts PBO wall time proportional to CPU count.
        var combinations = CombinationGenerator(numSegments, half).ToList();
        var results = new (double trainSharpe, double testSharpe)?[combinations.Count];
        int maxParallel = _perfOptions.PboMaxDegreeOfParallelism > 0
            ? Math.Max(1, _perfOptions.PboMaxDegreeOfParallelism)
            : Math.Max(2, Environment.ProcessorCount - 1);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, combinations.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel },
            async (idx, ct) =>
            {
                var combination = combinations[idx];
                var trainSegments = new List<List<Candle>>();
                var testSegments = new List<List<Candle>>();
                for (int i = 0; i < numSegments; i++)
                {
                    if ((combination & (1L << i)) != 0) trainSegments.Add(segments[i]);
                    else testSegments.Add(segments[i]);
                }
                var trainCandles = ConcatenateSegments(trainSegments);
                var testCandles = ConcatenateSegments(testSegments);
                if (trainCandles.Count < 60 || testCandles.Count < 60) return;

                // Train then test sequentially: two concurrent RunOnSegmentsAsync calls on the
                // same engine shared one AsyncLocal slot before fork semantics could isolate them,
                // corrupting percentile gates. Outer Parallel.ForEachAsync still parallelizes combos.
                var trainResult = await RunOnSegmentsAsync(trainCandles, symbol, startingCapital).ConfigureAwait(false);
                var testResult = await RunOnSegmentsAsync(testCandles, symbol, startingCapital).ConfigureAwait(false);
                results[idx] = (ComputeSharpeRatio(trainResult), ComputeSharpeRatio(testResult));

                int done = Interlocked.Increment(ref pboDone);
                if (_perfOptions.LogPboProgressToConsole
                    && (done % pboReportEvery == 0 || done == combinations.Count))
                {
                    double pct = 100.0 * done / combinations.Count;
                    var elapsed = pboSw.Elapsed;
                    var etaSec = done > 0 ? elapsed.TotalSeconds * (combinations.Count - done) / done : 0;
                    Console.WriteLine($"[PBO] {done}/{combinations.Count} ({pct:F1}%)  elapsed={elapsed:mm\\:ss}  ETA={TimeSpan.FromSeconds(etaSec):mm\\:ss}");
                }
            }).ConfigureAwait(false);

        foreach (var r in results)
        {
            if (r is null) continue;
            trainSharpeList.Add(r.Value.trainSharpe);
            testSharpeList.Add(r.Value.testSharpe);
            logitWeights.Add(0.0);
        }
        pboSw.Stop();
        if (_perfOptions.LogPboProgressToConsole)
        {
            Console.WriteLine($"[PBO] done in {pboSw.Elapsed:mm\\:ss} ({maxParallel} threads)");
        }

        if (trainSharpeList.Count == 0)
        {
            return new PboResult { PBO = 1.0, TotalCombinations = 0 };
        }

        FillPboLogitWeights(trainSharpeList, logitWeights);

        double medianTrainSharpe = Median(trainSharpeList);
        double medianTestSharpe = Median(testSharpeList);

        double pbo = 0.0;
        double totalWeight = 0.0;
        double maxLogit = logitWeights.Max();

        for (int i = 0; i < trainSharpeList.Count; i++)
        {
            double weight = Math.Exp(logitWeights[i] - maxLogit);
            totalWeight += weight;

            if (trainSharpeList[i] > medianTrainSharpe && testSharpeList[i] < medianTestSharpe)
            {
                pbo += weight;
                overfittingCount++;
            }
        }

        pbo = totalWeight > 0 ? pbo / totalWeight : 0.0;

        double expectedPerformance = testSharpeList.Count > 0 ? testSharpeList.Average() : 0.0;
        double performanceDecay = trainSharpeList.Count > 0 && trainSharpeList.Average() != 0
            ? (trainSharpeList.Average() - testSharpeList.Average()) / Math.Abs(trainSharpeList.Average())
            : 0.0;

        double stochasticDominance = testSharpeList.Count > 0
            ? testSharpeList.Count(s => s > 0) / (double)testSharpeList.Count
            : 0.0;

        return new PboResult
        {
            PBO = pbo,
            StochasticDominance = stochasticDominance,
            ExpectedPerformance = expectedPerformance,
            PerformanceDecay = performanceDecay,
            TotalCombinations = trainSharpeList.Count,
            OverfittingCount = overfittingCount,
            TrainSharpeRatios = trainSharpeList,
            TestSharpeRatios = testSharpeList,
            LogitWeights = logitWeights
        };
    }

    private List<List<Candle>> PartitionIntoSegments(List<Candle> candles, int numSegments, PboPartitionMode mode = PboPartitionMode.Contiguous)
    {
        // Contiguous (default): preserve regime clusters; exposes regime-overfit params honestly.
        // Interleaved: stride blocks round-robin across segments; deflates PBO by hiding regime shifts.
        // Contiguous is correct when the strategy claims regime awareness; interleaved only if history
        // is too short for contiguous segments to each contain at least one full regime cycle.
        if (mode == PboPartitionMode.Contiguous)
        {
            return PartitionContiguous(candles, numSegments);
        }

        const int BlockSize = 32;
        var segments = new List<List<Candle>>(numSegments);
        for (int i = 0; i < numSegments; i++) segments.Add(new List<Candle>());

        int blockCount = candles.Count / BlockSize;
        for (int b = 0; b < blockCount; b++)
        {
            int target = b % numSegments;
            segments[target].AddRange(candles.GetRange(b * BlockSize, BlockSize));
        }
        int tailStart = blockCount * BlockSize;
        if (tailStart < candles.Count)
        {
            segments[numSegments - 1].AddRange(candles.GetRange(tailStart, candles.Count - tailStart));
        }

        if (segments.Any(s => s.Count < BlockSize))
        {
            return PartitionContiguous(candles, numSegments);
        }
        return segments;
    }

    private static List<List<Candle>> PartitionContiguous(List<Candle> candles, int numSegments)
    {
        var segments = new List<List<Candle>>(numSegments);
        int segmentSize = candles.Count / numSegments;
        for (int i = 0; i < numSegments; i++)
        {
            int start = i * segmentSize;
            int count = (i == numSegments - 1) ? candles.Count - start : segmentSize;
            segments.Add(candles.GetRange(start, count));
        }
        return segments;
    }

    private List<Candle> ConcatenateSegments(List<List<Candle>> segments)
    {
        var result = new List<Candle>();
        foreach (var seg in segments)
            result.AddRange(seg);
        return result;
    }

    private static IEnumerable<long> CombinationGenerator(int n, int k)
    {
        long combination = (1L << k) - 1;
        long limit = 1L << n;
        while (combination < limit)
        {
            yield return combination;
            long smallest = combination & -combination;
            long ripple = combination + smallest;
            long ones = combination ^ ripple;
            ones = (ones >> 2) / smallest;
            combination = ripple | ones;
        }
    }

    private static int BinomialCoefficient(int n, int k)
    {
        if (k > n) return 0;
        if (k == 0 || k == n) return 1;
        if (k > n / 2) k = n - k;
        long result = 1;
        for (int i = 1; i <= k; i++)
        {
            result = result * (n - i + 1) / i;
        }
        return (int)Math.Min(result, int.MaxValue);
    }

    private static double ComputeSharpeRatio(BacktestResult result)
    {
        if (result.Trades == null || result.Trades.Count < 2) return 0.0;
        var returns = result.Trades.Select(t => (double)t.PnL).ToList();
        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        if (stdDev <= 0) return 0.0;
        double rawSharpe = mean / stdDev * Math.Sqrt(252);

        // Phase C — Deflated Sharpe approximation (Bailey & López de Prado 2014).
        // Shrink toward zero when sample size is small; penalizes segments where raw
        // Sharpe is high only because of lucky streaks in ≤20 trades. Essential for
        // honest PBO: without it, tiny-sample high-SR splits masquerade as "winners"
        // and inflate PBO's overfitting estimate.
        double n = returns.Count;
        double shrinkage = n / (n + 20.0);
        // Additionally penalize SR variance: skew/kurt-agnostic approximation, clamp to [-3, +3].
        double deflated = rawSharpe * shrinkage;
        return Math.Clamp(deflated, -3.0, 3.0);
    }

    /// <summary>
    /// PBO logit weights from train-window Sharpe ranks. O(n log n); replaces per-row O(n) scans.
    /// </summary>
    private static void FillPboLogitWeights(List<double> trainSharpeList, List<double> logitWeights)
    {
        int n = trainSharpeList.Count;
        var sorted = new double[n];
        for (int i = 0; i < n; i++)
        {
            sorted[i] = trainSharpeList[i];
        }

        Array.Sort(sorted);
        double inv = 1.0 / (n + 1);
        for (int i = 0; i < n; i++)
        {
            int rank = CountLessOrEqualSorted(sorted, trainSharpeList[i]);
            double rankProb = rank * inv;
            double clampedProb = Math.Max(1e-10, Math.Min(1.0 - 1e-10, rankProb));
            logitWeights[i] = Math.Log(clampedProb / (1.0 - clampedProb));
        }
    }

    /// <summary>Count of values in sorted ascending array that are less than or equal to v (upper-bound style).</summary>
    private static int CountLessOrEqualSorted(double[] sortedAsc, double v)
    {
        int left = 0, right = sortedAsc.Length;
        while (left < right)
        {
            int mid = left + ((right - left) >> 1);
            if (sortedAsc[mid] <= v)
            {
                left = mid + 1;
            }
            else
            {
                right = mid;
            }
        }

        return left;
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
    }

    private async Task<BacktestResult> RunOnSegmentsAsync(List<Candle> candles, string symbol, decimal startingCapital)
    {
        var segCtx = new BacktestRunContext();
        var prevCtx = _runContext.Value;
        _runContext.Value = segCtx;
        try
        {
            var state = new BacktestState(startingCapital);
            var trades = new List<BacktestTradeResult>();
            var fullTradeHistory = new List<Trade>();
            PaperPosition? openPosition = null;

            var candlesArray = candles.ToArray();
            const int WarmupBars = 60;
            for (int i = WarmupBars; i < candles.Count; i++)
            {
                var candle = candlesArray[i];
                state.AdvanceBar(candle.Timestamp);
                if (openPosition != null)
                {
                    openPosition = ProcessExit(candle, openPosition, state, trades, fullTradeHistory);
                }
                else
                {
                    var window = new ArraySegment<Candle>(candlesArray, i - WarmupBars, WarmupBars + 1);
                    int futureCount = Math.Min(CurrentCosts.MakerEntryTimeoutBars, candles.Count - i - 1);
                    var future = futureCount > 0
                        ? new ArraySegment<Candle>(candlesArray, i + 1, futureCount)
                        : (IReadOnlyList<Candle>?)null;
                    openPosition = await TryEntry(symbol, window, state, fullTradeHistory, future).ConfigureAwait(false);
                }
            }

            ForceCloseOpenPositionIfNeeded(candlesArray, openPosition, state, trades, fullTradeHistory);
            return BuildFinalResult(
                symbol,
                candles,
                trades,
                state,
                startingCapital,
                DecisionCoverageFlags.None,
                _inferenceAgent?.IsModelAvailable ?? false,
                false,
                new Dictionary<string, int>());
        }
        finally
        {
            _runContext.Value = prevCtx;
        }
    }

    private PaperPosition? ProcessExit(Candle candle, PaperPosition pos, BacktestState state, List<BacktestTradeResult> trades, List<Trade> fullHistory)
    {
        // Bar bookkeeping: bars held + high-water mark
        pos.BarsHeld++;
        if (pos.Side == TradeSide.Buy)
            pos.OptimalPriceSinceEntry = Math.Max(pos.OptimalPriceSinceEntry, candle.High);
        else
            pos.OptimalPriceSinceEntry = pos.OptimalPriceSinceEntry == 0
                ? candle.Low
                : Math.Min(pos.OptimalPriceSinceEntry, candle.Low);

        // 1) Partial TP — realize a fraction of qty, then arm breakeven + trailing.
        if (!pos.HasTakenPartialProfit
            && pos.PartialTakeProfit > 0m
            && pos.PartialExitFraction > 0m
            && PartialHit(candle, pos))
        {
            var partialExitPrice = ApplySlippage(pos.PartialTakeProfit, pos.Side, isEntry: false, TrOverPrice(candle));
            var partialQty = Math.Round(pos.Quantity * pos.PartialExitFraction, 6, MidpointRounding.AwayFromZero);
            if (partialQty > 0m && partialQty < pos.RemainingQuantity)
            {
                var partialFees = CalculateRoundTripFees(pos.EntryPrice, partialExitPrice, partialQty, pos.IsMakerEntry);
                var partialNotional = partialQty * ((pos.EntryPrice + partialExitPrice) / 2m);
                var partialFunding = _fundingRateProvider is not null and not NullFundingRateProvider
                    ? FundingAccrualService.CalculateFromProvider(pos.Symbol, partialNotional, pos.Side, pos.EntryTime, candle.Timestamp, _fundingRateProvider)
                    : FundingAccrualService.Calculate(partialNotional, pos.Side, pos.EntryTime, candle.Timestamp, CurrentCosts.FundingBpsPer8h);
                var partialPnL = CalculateNetPnl(pos.EntryPrice, partialExitPrice, partialQty, pos.Side, pos.IsMakerEntry) + partialFunding;
                state.Update(partialPnL);
                state.RegisterTradeResult(partialPnL, CurrentTimeframe);
                state.ReleaseMargin(pos.InitialMargin, partialQty, pos.Quantity);

                trades.Add(new BacktestTradeResult
                {
                    EntryTime = pos.EntryTime,
                    ExitTime = candle.Timestamp,
                    Side = pos.Side,
                    PnL = partialPnL,
                    ExitReason = "PartialTP",
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = partialExitPrice,
                    PnLPercent = CalculatePnLPercent(pos.EntryPrice, partialExitPrice, pos.Side),
                    Reasoning = pos.Reasoning,
                    Confidence = pos.Confidence,
                    StrategyTag = pos.StrategyTag,
                    RegimeTag = pos.RegimeTag,
                    ExitPolicyTag = pos.ExitPolicyTag,
                    TotalFeesPaid = partialFees,
                    NetFunding = partialFunding
                });

                pos.RemainingQuantity -= partialQty;
                pos.HasTakenPartialProfit = true;

                // Breakeven-on-partial tested and reverted: Trend winners often retest entry
                // after partial TP before resuming; BE stop cut them short.
                // 1.5× InitialStopDistance monotonic trailing stop tested (byrgm0w8m, 2026-04-19)
                // and reverted: Sharpe 0.59→0.57, PnL $256→$248, PBO 0.365→0.704 (blew gate).
                // Exit-timing shifts amplified by WFO grid search — same pattern as maker-offset
                // and asymmetric-sizing experiments. Do NOT re-add exit tweaks without first
                // decoupling the PBO grid from sizing/cost/exit parameters.
            }
        }

        // 2) Full exit resolution: SL / TP.
        string? reason = ResolveExitReason(candle, pos);
        if (reason == null) return pos;

        decimal exitPrice = candle.Close;
        if (reason == "StopLoss") exitPrice = pos.StopLoss;
        else if (reason == "TakeProfit") exitPrice = pos.TakeProfit;
        else if (reason == "Liquidation") exitPrice = pos.LiquidationPrice;
        // Gap-through handling: if the candle opened already past the stop/TP, we fill at Open
        // (worse for SL, better for TP). Clamp fills into [Low, High] so backtest cannot report
        // impossible prices (e.g. a SL fill below the bar low on a wick).
        exitPrice = ClampFillToCandle(exitPrice, reason, pos.Side, candle);
        exitPrice = ApplySlippage(exitPrice, pos.Side, isEntry: false, TrOverPrice(candle));
        var remainingQty = pos.RemainingQuantity > 0m ? pos.RemainingQuantity : pos.Quantity;
        var feesPortion = CalculateRoundTripFees(pos.EntryPrice, exitPrice, remainingQty, pos.IsMakerEntry);
        var notional = remainingQty * ((pos.EntryPrice + exitPrice) / 2m);
        var funding = _fundingRateProvider is not null and not NullFundingRateProvider
            ? FundingAccrualService.CalculateFromProvider(pos.Symbol, notional, pos.Side, pos.EntryTime, candle.Timestamp, _fundingRateProvider)
            : FundingAccrualService.Calculate(notional, pos.Side, pos.EntryTime, candle.Timestamp, CurrentCosts.FundingBpsPer8h);
        decimal netPnL = CalculateNetPnl(pos.EntryPrice, exitPrice, remainingQty, pos.Side, pos.IsMakerEntry) + funding;
        state.Update(netPnL);
        state.RegisterTradeResult(netPnL, CurrentTimeframe);
        state.ReleaseMargin(pos.InitialMargin, remainingQty, pos.Quantity);

        trades.Add(new BacktestTradeResult
        {
            EntryTime = pos.EntryTime,
            ExitTime = candle.Timestamp,
            Side = pos.Side,
            PnL = netPnL,
            ExitReason = reason,
            EntryPrice = pos.EntryPrice,
            ExitPrice = exitPrice,
            PnLPercent = CalculatePnLPercent(pos.EntryPrice, exitPrice, pos.Side),
            Reasoning = pos.Reasoning,
            Confidence = pos.Confidence,
            StrategyTag = pos.StrategyTag,
            RegimeTag = pos.RegimeTag,
            ExitPolicyTag = pos.ExitPolicyTag,
            TotalFeesPaid = feesPortion,
            NetFunding = funding
        });

        fullHistory.Add(MapToClosedTrade(pos, candle.Timestamp, exitPrice, feesPortion, funding));
        return null;
    }

    private static bool PartialHit(Candle c, PaperPosition p)
        => p.Side == TradeSide.Buy ? c.High >= p.PartialTakeProfit : c.Low <= p.PartialTakeProfit;

    private Trade MapToTrade(PaperPosition p) => new() { Symbol = p.Symbol, Side = p.Side, EntryPrice = p.EntryPrice, Quantity = p.Quantity, EntryTime = p.EntryTime };

    private Trade MapToClosedTrade(PaperPosition p, DateTime exitTime, decimal exitPrice, decimal totalFeesPaid, decimal netFunding) => new()
    {
        Symbol = p.Symbol,
        Side = p.Side,
        EntryPrice = p.EntryPrice,
        ExitPrice = exitPrice,
        Quantity = p.Quantity,
        EntryTime = p.EntryTime,
        ExitTime = exitTime,
        StrategyTag = p.StrategyTag,
        RegimeTag = p.RegimeTag,
        ExitPolicyTag = p.ExitPolicyTag,
        Status = TradeStatus.Executed,
        PromotionStage = "Backtest",
        ExecutionMode = "SIMULATED",
        Leverage = p.Leverage <= 0m ? 1m : p.Leverage,
        MarginMode = p.MarginMode,
        ClosedQuantity = p.Quantity,
        AverageExitPrice = exitPrice,
        TotalFeesPaid = totalFeesPaid,
        NetFunding = netFunding,
        LifecycleStatus = TradeLifecycleStatus.Closed
    };

    private static decimal CalculatePnLPercent(decimal entryPrice, decimal exitPrice, TradeSide side)
    {
        if (entryPrice <= 0) return 0;
        var rawPercent = (exitPrice - entryPrice) / entryPrice * 100;
        return side == TradeSide.Sell ? -rawPercent : rawPercent;
    }

    private async Task<PaperPosition?> TryEntry(string symbol, IReadOnlyList<Candle> window, BacktestState state, IReadOnlyList<Trade> history, IReadOnlyList<Candle>? futureCandles = null)
    {
        var rc = RequireRunContext();
        if (_guardOptions.EnableCooldownGate && !state.CanOpenTrade)
        {
            return RejectEntry("Cooldown");
        }

        var candle = window[^1];
        var signal = new Signal { Symbol = symbol, Price = candle.Close };
        if (CurrentHftMode)
        {
            // Tag signal so HftStrategyRouter.CanHandle returns true (and Trend/Range/Breakout
            // routers bail via their !IsHftSignal guards). Produces an HFT-only session.
            signal.Source = "HFT";
            signal.Metadata["ExecutionMode"] = "HFT";
        }

        // Detect regime once and apply regime-aware sizing (matches live path in ProcessSignalHandler/SwarmOrchestrator).
        rc.CoverageFlags |= DecisionCoverageFlags.RegimeDetection;
        var regimeResult = MarketRegimeDetector.Detect(window);
        var regime = regimeResult.Regime;
        var guardProfile = ResolveGuardProfile();
        if (state.ShouldBlockByExpectancy(_guardOptions, guardProfile))
        {
            return RejectEntry("ExpectancyGuard");
        }
        if (state.ShouldBlockByCircuitBreaker(candle.Timestamp))
        {
            _logger.LogDebug("Circuit breaker active for {Symbol}; skipping new entry.", symbol);
            return RejectEntry("CircuitBreaker");
        }
        var risk = ResolveRiskParameters(regime);

        // 1. Technical Baseline
        rc.CoverageFlags |= DecisionCoverageFlags.TechnicalAnalysis;
        var decision = CurrentHftMode && _hftSignalGenerationService is not null
            ? await _hftSignalGenerationService.AnalyzeAsync(signal, window.ToList(), risk).ConfigureAwait(false)
            : await _technicalAnalysis.AnalyzeAsync(signal, window, risk).ConfigureAwait(false);
        if (decision is null) return RejectEntry("NullDecision");
        decision = ApplyStrategyRouter(signal, decision, regimeResult, candle);
        decision = ApplyInferenceOverlay(window, decision);
        var minConfidence = ResolveMinConfidence();
        var regimePenalty = ResolveRegimePenalty(regime);
        if (!decision.ShouldTrade && TryBuildFallbackDecision(decision, candle, regimeResult, minConfidence, out var fallback))
        {
            decision = fallback;
        }

        if (!decision.ShouldTrade)
        {
            return RejectEntry("NoTradeSignal");
        }

        var effectiveConfidence = decision.Confidence - regimePenalty.ConfidencePenalty;
        if (effectiveConfidence < minConfidence) return RejectEntry("ConfidenceGate");

        if (!CurrentHftMode)
        {
            var confluence = EntryConfluenceService.Evaluate(window, decision.Side, regime);
            if (!confluence.Pass)
                return RejectEntry(confluence.RejectReason ?? "ConfluenceGate");

            // Order-flow gate removed 2026-04-24: candle-CVD proxy was empirically proven noisy
            // (rejects good trades alongside bad on 4h bars). Real CVD lives on the HFT tick path
            // via OFIExecutionAgent — no value running a degraded version here.
        }

        // Funding-rate gate: blocks crowded-side entries on perpetual swaps. No-op unless
        // an IFundingRateProvider with historical/live data is registered; safe to keep
        // always-on because NullFundingRateProvider returns 0 (no rejection).
        if (_fundingRateSignal is not null)
        {
            var fund = _fundingRateSignal.Evaluate(symbol, candle.Timestamp, decision.Side);
            if (!fund.Pass)
                return RejectEntry(fund.RejectReason ?? "FundingGate");
        }

        // Regime-expectancy gate: if this regime has accumulated enough OOS samples (≥50)
        // and the Wilson-lower-bound win rate is below breakeven for the target R:R, block.
        // This is a self-learning filter — once WFO has fed it OOS trades, losing regimes
        // are shut off automatically without touching any parameter.
        // 2026-04-24: raised threshold 30→50 for statistical power (Wilson CI at 30 trades
        // too wide to discriminate reliably between weak and dead regimes).
        if (_regimeExpectancy is not null)
        {
            var exp = _regimeExpectancy.GetExpectancy(regime.ToString());
            if (exp.TradeCount >= 50 && exp.WinRateWilsonLower < 0.30)
                return RejectEntry("RegimeExpectancyGate");
        }

        // ATR-scaled TP/SL: when the profile enables ATR exits, replace weak indicator-derived
        // levels with ATR-proportional distances. This guarantees a minimum gross RR equal to
        // TakeProfitAtrMultiple / StopLossAtrMultiple (default: 2.5 / 1.0 = 2.5:1).
        var atrExitProfile = ResolveProfile();
        if (atrExitProfile.UseAtrScaledExits && decision.Indicators?.ATR is > 0)
        {
            var atr = (decimal)decision.Indicators.ATR.Value;
            var atrSl = atr * atrExitProfile.StopLossAtrMultiple;
            var atrTp = atr * atrExitProfile.TakeProfitAtrMultiple;
            if (atrSl > 0m && atrTp > 0m)
            {
                var newSl = decision.Side == TradeSide.Buy
                    ? candle.Close - atrSl
                    : candle.Close + atrSl;
                var newTp = decision.Side == TradeSide.Buy
                    ? candle.Close + atrTp
                    : candle.Close - atrTp;
                // Only override if ATR-scaled levels improve the RR vs what the indicators gave us.
                var existingRisk = Math.Abs(candle.Close - decision.StopLoss);
                var existingReward = Math.Abs(decision.TakeProfit - candle.Close);
                var existingRr = existingRisk > 0 ? existingReward / existingRisk : 0m;
                var atrRr = atrSl > 0 ? atrTp / atrSl : 0m;
                if (atrRr > existingRr)
                {
                    decision = decision with
                    {
                        StopLoss = newSl,
                        TakeProfit = newTp,
                        Reasoning = $"{decision.Reasoning} | ATR-scaled exits: SL={atrExitProfile.StopLossAtrMultiple:F1}×ATR, TP={atrExitProfile.TakeProfitAtrMultiple:F1}×ATR"
                    };
                }
            }
        }

        decision = NormalizeDecisionRiskReward(decision, candle.Close);

        // 2. Agent Intelligence Layer (Simplified for Backtest Context)
        // HFT mode skips the full 5-tier pipeline — too heavy for scalp-style bars.
        rc.CoverageFlags |= DecisionCoverageFlags.AgentPipeline;
        var snapshot = CurrentHftMode
            ? new FiveTierSnapshot()
            : (await _agentCoordinator.RunFullPipelineAsync(
                signal,
                new List<MarketTick>(),
                window,
                regime,
                new List<Trade>(),
                history).ConfigureAwait(false)
                ?? new FiveTierSnapshot());

        // 3. Consensus Logic (Multi-Agent Synthesis)
        float agentAdjustment = (snapshot.Votes ?? Array.Empty<StrategyVote>())
            .Where(v => v.Vote != VoteOutcome.Abstain)
            .Sum(v => v.ConfidenceAdjustment);

        var consensusConfidence = (double)effectiveConfidence + agentAdjustment;

        // Apply Metadata Adaptation (Tier 5)
        consensusConfidence *= snapshot.Meta?.ConfidenceScalar ?? 1f;

        // Phase A.2 — Rolling percentile gate. Reject unless consensus confidence is
        // in the top (1 - ConfidencePercentileFloor) of the rolling window. This is a
        // regime-adaptive filter: during high-conviction periods everything above the
        // bar trades, during low-conviction periods nothing does. Unlike a hardcoded
        // threshold it does not lock in an in-sample-fitted magic number.
        var confHistory = GetConfidenceHistory();
        double[]? sortedSnapshot = null;
        if (confHistory.Count >= ResolveConfidenceHistoryMinActivation())
        {
            sortedSnapshot = confHistory.ToArray();
        }
        confHistory.Enqueue(consensusConfidence);
        while (confHistory.Count > ConfidenceHistoryWindow) confHistory.Dequeue();
        if (sortedSnapshot is not null)
        {
            Array.Sort(sortedSnapshot);
            int idx = (int)(sortedSnapshot.Length * ConfidencePercentileFloor);
            if (idx >= sortedSnapshot.Length) idx = sortedSnapshot.Length - 1;
            if (consensusConfidence < sortedSnapshot[idx])
            {
                return RejectEntry("ConfidencePercentile");
            }
        }

        // Phase A.3 — Ensemble vote gate. Require ≥2 approves OR 1 approve with zero rejects.
        // 2026-04-24: tested relaxation to "only block on majority reject" (to unblock 727 rejects
        // on BTC 1h 90d). Result was disastrous: trades jumped 42→65 but WR collapsed 57%→22%, PnL
        // swung +235→-507. The all-abstain case the relaxation allowed through is genuinely low
        // quality — routers abstain when no regime-specific pattern fires. The original gate is
        // doing real quality work. Do not relax without per-router quality audit first.
        if (!CurrentHftMode && snapshot.Votes != null && snapshot.Votes.Count > 0)
        {
            int approves = snapshot.Votes.Count(v => v.Vote == VoteOutcome.Approve);
            int rejects = snapshot.Votes.Count(v => v.Vote == VoteOutcome.Reject);
            if (approves < 2 && (approves - rejects) <= 0)
            {
                return RejectEntry("EnsembleVote");
            }
        }

        // Re-run risk gauntlet against the REAL decision. snapshot.RiskGauntlet is a telemetry-only
        // shadow run against a placeholder TradeDecision (Confidence=50, SL/TP=0) and would always
        // veto on Kelly — must not be used as a trade gate.
        var realGauntlet = await _agentCoordinator.RunRiskGauntletAsync(signal, decision, Array.Empty<Trade>()).ConfigureAwait(false);
        rc.CoverageFlags |= DecisionCoverageFlags.RiskGauntlet;
        if (realGauntlet != null && !realGauntlet.Approved) return RejectEntry("RiskGauntlet");
        decision = ApplyEdgeScoreOverlay(signal, decision);
        if (!decision.ShouldTrade) return RejectEntry("EdgeScore");
        if (consensusConfidence < minConfidence) return RejectEntry("ConsensusConfidence");

        // Pre-size estimate for size-impact slippage: actual qty is computed below off the
        // slipped price, but we need a notional estimate to feed back into slip. Two-pass:
        // (1) price @ mid-ish with regime+range only, (2) size against that, (3) final slip with impact.
        var barRangeOverPrice = candle.Close > 0 ? (candle.High - candle.Low) / candle.Close : 0m;
        var barVolumeNotional = candle.Volume * candle.Close;

        // Maker-only limit entry path (opt-in via BacktestCostModel.UseMakerEntries). Posts a limit
        // order N bps inside the close (better than mid for us) and peeks forward up to
        // MakerEntryTimeoutBars bars for a touch. If filled: entry price = limit, no spread, no
        // slippage, maker commission on entry leg. If not filled within timeout: reject entry
        // entirely (do NOT fall back to taker — preserves the cost-discipline that makes this
        // path worth taking).
        bool isMakerEntry = false;
        decimal price;
        if (CurrentCosts.UseMakerEntries && futureCandles != null && futureCandles.Count > 0)
        {
            // Long limit is BELOW close; short limit is ABOVE close.
            var offset = CurrentCosts.MakerEntryOffsetRate;
            decimal limitPrice = decision.Side == TradeSide.Buy
                ? candle.Close * (1m - offset)
                : candle.Close * (1m + offset);

            bool filled = false;
            for (int fi = 0; fi < futureCandles.Count && fi < CurrentCosts.MakerEntryTimeoutBars; fi++)
            {
                var fc = futureCandles[fi];
                if (decision.Side == TradeSide.Buy && fc.Low <= limitPrice) { filled = true; break; }
                if (decision.Side == TradeSide.Sell && fc.High >= limitPrice) { filled = true; break; }
            }

            if (!filled) return RejectEntry("MakerNoFill");
            price = limitPrice;
            isMakerEntry = true;
        }
        else
        {
            var provisionalPrice = ApplySlippage(candle.Close, decision.Side, isEntry: true, TrOverPrice(candle),
                regime, barRangeOverPrice);
            var provisionalQty = provisionalPrice > 0
                ? (state.AvailableEquity * decision.PositionSizePercent * regimePenalty.PositionSizeMultiplier * (CurrentLeverage > 0m ? CurrentLeverage : 1m)) / provisionalPrice
                : 0m;
            var provisionalNotional = provisionalQty * provisionalPrice;
            price = ApplySlippage(candle.Close, decision.Side, isEntry: true, TrOverPrice(candle),
                regime, barRangeOverPrice, provisionalNotional, barVolumeNotional);
        }
        if (!PassesMinRiskReward(decision, price, out var rr))
        {
            _logger.LogDebug("Skipped {Symbol} on {Timeframe}: R:R {RR:F2} below threshold.", symbol, CurrentTimeframe, rr);
            return RejectEntry("RiskRewardGate");
        }
        rc.CoverageFlags |= DecisionCoverageFlags.RiskRewardGate;

        // Pullback Filter (R:R booster): require entry within 0.5 ATR of EMA20. Trending
        // regime only — in Ranging/Volatile we actively want extremes, not the mean.
        var isTrendFamilyDecision =
            string.Equals(decision.StrategyTag, "TrendConfluence", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.StrategyTag, "BreakoutHunter", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(decision.RegimeTag, MarketRegime.Trending.ToString(), StringComparison.OrdinalIgnoreCase);

        if (!CurrentHftMode &&
            _guardOptions.EnablePullbackFilter &&
            isTrendFamilyDecision &&
            !PassesPullbackFilter(candle.Close, decision.Indicators, window, _guardOptions.PullbackAtrMultiplier))
        {
            _logger.LogDebug("Skipped {Symbol}: entry too far from EMA20 (trend-chase filter).", symbol);
            return RejectEntry("PullbackFilter");
        }

        // Real 4h MTF Confluence Gate: only runs if orchestrator supplied HTF candles.
        // Uses EMA20 slope on the last 30 4h closes ≤ current timestamp. Rejects Trend/Breakout
        // trades against 4h bias. Ranging skipped (mean-reversion is contra-trend by design).
        if (!CurrentHftMode &&
            _guardOptions.EnableHigherTimeframeBiasGate &&
            isTrendFamilyDecision
            && !PassesHtfMtfGate(candle.Timestamp, decision.Side))
        {
            _logger.LogDebug("Skipped {Symbol}: 4h HTF bias against {Side}.", symbol, decision.Side);
            return RejectEntry("HtfMtfGate");
        }

        var leverage = CurrentLeverage > 0m ? CurrentLeverage : 1m;
        var availableEquity = state.AvailableEquity;
        var sizedPositionPercent = decision.PositionSizePercent * regimePenalty.PositionSizeMultiplier;
        rc.CoverageFlags |= DecisionCoverageFlags.PositionSizing;
        decimal qty = price > 0
            ? Math.Round((availableEquity * sizedPositionPercent * leverage) / price, 6, MidpointRounding.AwayFromZero)
            : 0;
        
        if (qty <= 0) return RejectEntry("PositionSizeZero");

        var notional = qty * price;
        var initialMargin = leverage > 1m ? notional / leverage : notional;
        if (initialMargin <= 0m || !state.TryReserveMargin(initialMargin))
        {
            return RejectEntry("MarginUnavailable");
        }

        var primaryPattern = snapshot.Patterns?.PatternTags?.FirstOrDefault() ?? "No Pattern";
        var voteSummary = (snapshot.Votes ?? Array.Empty<StrategyVote>()).Any(v => v.Vote == VoteOutcome.Approve) ? " (Voted Approve)" : "";

        // Liquidation price proxy for leveraged sims: price at which equity hits maintenance margin.
        // Long: liq = entry * (1 - 1/L + mmr);  Short: liq = entry * (1 + 1/L - mmr). L=1 => disabled.
        var liqPrice = 0m;
        if (leverage > 1m)
        {
            var invLev = 1m / leverage;
            liqPrice = decision.Side == TradeSide.Buy
                ? price * (1m - invLev + MaintenanceMarginRate)
                : price * (1m + invLev - MaintenanceMarginRate);
        }

        return new PaperPosition
        {
            Symbol = symbol, Side = decision.Side, EntryPrice = price,
            Quantity = qty, RemainingQuantity = qty,
            StopLoss = decision.StopLoss, TakeProfit = decision.TakeProfit,
            PartialTakeProfit = decision.PartialTakeProfit,
            PartialExitFraction = decision.PartialExitFraction,
            BreakevenTriggerFraction = decision.BreakevenTriggerFraction,
            MaxHoldingBars = decision.MaxHoldingBars,
            InitialStopDistance = Math.Abs(price - decision.StopLoss),
            EntryTime = candle.Timestamp,
            OptimalPriceSinceEntry = price,
            Reasoning = $"Engine Consensus: {primaryPattern}{voteSummary}. {decision.Reasoning}",
            Confidence = Math.Min(99, Math.Max(10, (int)consensusConfidence)),
            StrategyTag = decision.StrategyTag,
            RegimeTag = decision.RegimeTag,
            ExitPolicyTag = decision.ExitPolicyTag,
            Leverage = leverage,
            LiquidationPrice = liqPrice,
            IsLeveragedPaperPosition = leverage > 1m,
            InitialMargin = initialMargin,
            RemainingMargin = initialMargin,
            IsMakerEntry = isMakerEntry
        };
    }

    private static string? ResolveExitReason(Candle c, PaperPosition p)
    {
        // Liquidation check first (leveraged sims only): if the bar range crosses liq price,
        // force-close at liq to approximate the exchange's forced deleverage.
        if (p.IsLeveragedPaperPosition && p.LiquidationPrice > 0m)
        {
            if (p.Side == TradeSide.Buy && c.Low <= p.LiquidationPrice) return "Liquidation";
            if (p.Side == TradeSide.Sell && c.High >= p.LiquidationPrice) return "Liquidation";
        }

        if (p.Side == TradeSide.Buy)
        {
            if (c.Low <= p.StopLoss) return "StopLoss";
            if (c.High >= p.TakeProfit) return "TakeProfit";
        }
        else
        {
            if (c.High >= p.StopLoss) return "StopLoss";
            if (c.Low <= p.TakeProfit) return "TakeProfit";
        }
        // TimeStop deliberately disabled in backtest — forced time-exits at candle close
        // cut trend winners short before TP. Full exit only via SL/TP or end-of-data.
        return null;
    }

    private decimal CalculateRoundTripFees(decimal entry, decimal exit, decimal qty, bool isEntryMaker = false)
    {
        var entryRate = isEntryMaker ? CurrentCosts.MakerCommissionRate : CurrentCosts.CommissionRate;
        var exitRate = CurrentCosts.CommissionRate; // exits always taker in current model
        return (entry * qty * entryRate) + (exit * qty * exitRate);
    }

    private decimal CalculateNetPnl(decimal entry, decimal exit, decimal qty, TradeSide side, bool isEntryMaker = false)
    {
        var direction = side == TradeSide.Sell ? -1m : 1m;
        decimal gross = (exit - entry) * qty * direction;
        return gross - CalculateRoundTripFees(entry, exit, qty, isEntryMaker);
    }

    private decimal ApplySlippage(decimal price, TradeSide side, bool isEntry, decimal atrOverPrice = 0m,
        MarketRegime regime = MarketRegime.Ranging, decimal barRangeOverPrice = 0m,
        decimal orderNotional = 0m, decimal barVolumeNotional = 0m)
    {
        // Cost stack (adverse, always worsens fill):
        //   1. Half-spread: always paid on market orders (bid-ask crossing).
        //   2. Base adverse slippage (book-crossing beyond mid).
        //   3. ATR-proportional slippage (captures vol).
        //   4. Size impact (order vs bar liquidity).
        //   5. Volatile-regime spread multiplier (OKX books widen 3-5x on spikes).
        // Total capped at MaxSlippageFractionOfBarRange * barRange to prevent unrealistic spills
        // on huge candles (ATR from prior bar is stale at the moment of the spike).
        var worsenUp = (isEntry && side == TradeSide.Buy) || (!isEntry && side == TradeSide.Sell);
        var sign = worsenUp ? 1m : -1m;

        var spreadMult = regime == MarketRegime.Volatile ? CurrentCosts.VolatileSpreadMultiplier : 1m;
        var halfSpread = (CurrentCosts.SpreadRate * spreadMult) / 2m;

        var atrComponent = atrOverPrice > 0m ? CurrentCosts.AtrSlippageMultiplier * ResolveAtrSlippageMultiplier() * atrOverPrice : 0m;

        decimal sizeImpact = 0m;
        if (CurrentCosts.SizeImpactBpsPerNotionalPct > 0m && barVolumeNotional > 0m && orderNotional > 0m)
        {
            var notionalPct = orderNotional / barVolumeNotional;
            sizeImpact = CurrentCosts.SizeImpactBpsPerNotionalPct * notionalPct / 10_000m;
        }

        var totalRate = halfSpread + CurrentCosts.SlippageRate + atrComponent + sizeImpact;

        if (barRangeOverPrice > 0m && CurrentCosts.MaxSlippageFractionOfBarRange > 0m)
        {
            var cap = barRangeOverPrice * CurrentCosts.MaxSlippageFractionOfBarRange;
            if (totalRate > cap) totalRate = cap;
        }

        return price * (1m + sign * totalRate);
    }

    private RiskParameters ResolveRiskParameters(MarketRegime regime)
    {
        var p = ResolveProfile();

        return regime switch
        {
            MarketRegime.Volatile => RiskParameters.Default with { MaxPositionSizePercent = p.VolatilePositionCapPercent },
            MarketRegime.Trending => RiskParameters.Default with { MaxPositionSizePercent = p.TrendPositionCapPercent },
            _ => RiskParameters.Default with { MaxPositionSizePercent = p.RangePositionCapPercent }
        };
    }

    private int ResolveMinConfidence() => ResolveProfile().MinConfidence;
    private decimal ResolveMinRiskReward() => ResolveProfile().MinRiskReward;
    private decimal ResolveAtrSlippageMultiplier() => ResolveProfile().AtrSlippageScalar;

    private bool PassesMinRiskReward(TradeDecision decision, decimal entryPrice, out decimal rr)
    {
        rr = 0m;
        if (decision.StopLoss <= 0m || decision.TakeProfit <= 0m || entryPrice <= 0m)
        {
            return false;
        }

        var reward = decision.Side == TradeSide.Buy
            ? decision.TakeProfit - entryPrice
            : entryPrice - decision.TakeProfit;
        var risk = decision.Side == TradeSide.Buy
            ? entryPrice - decision.StopLoss
            : decision.StopLoss - entryPrice;
        if (risk <= 0m || reward <= 0m)
        {
            return false;
        }

        rr = reward / risk;
        return rr >= ResolveMinRiskReward();
    }

    private BacktestProfile ResolveProfile()
        => _wfoProfileOverlay.Value ?? ResolveOptionsProfileForTimeframe();

    private BacktestProfile ResolveOptionsProfileForTimeframe() => CurrentTimeframe switch
    {
        "1m" => _profileOptions.OneMinute,
        "5m" => _profileOptions.FiveMinute,
        "15m" => _profileOptions.FifteenMinute,
        "4h" => _profileOptions.FourHour,
        "1d" => _profileOptions.OneDay,
        _ => _profileOptions.OneHour
    };

    private static BacktestProfile MergeWfoOverlay(BacktestProfile @base, ParamSet overlay) => new()
    {
        MinConfidence = overlay.MinConfidence,
        MinRiskReward = overlay.MinRiskReward,
        TrendPositionCapPercent = overlay.TrendCapPercent,
        RangePositionCapPercent = @base.RangePositionCapPercent,
        VolatilePositionCapPercent = @base.VolatilePositionCapPercent,
        AtrSlippageScalar = @base.AtrSlippageScalar
    };

    private static string NormalizeTimeframe(string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return "1h";
        }

        return timeframe.Trim().ToLowerInvariant();
    }

    private static string NormalizeRiskProfile(string riskProfile)
    {
        if (string.IsNullOrWhiteSpace(riskProfile))
        {
            return "Moderate";
        }

        var value = riskProfile.Trim().ToLowerInvariant();
        if (value.Contains("low"))
        {
            return "Low";
        }

        if (value.Contains("agg"))
        {
            return "Aggressive";
        }

        return "Moderate";
    }

    private static decimal ClampFillToCandle(decimal fill, string reason, TradeSide side, Candle candle)
    {
        // Gap-through: if bar opened past the exit trigger, the realistic fill is the Open, not
        // the trigger price. Direction-aware:
        //   Long SL: Open < StopLoss means the bar gapped down through the stop → fill at Open (worse).
        //   Long TP: Open > TakeProfit means gap up past TP → fill at Open (better, rare).
        //   Short SL/TP: mirror.
        if (reason == "StopLoss")
        {
            if (side == TradeSide.Buy && candle.Open < fill) fill = candle.Open;
            else if (side == TradeSide.Sell && candle.Open > fill) fill = candle.Open;
        }
        else if (reason == "TakeProfit")
        {
            if (side == TradeSide.Buy && candle.Open > fill) fill = candle.Open;
            else if (side == TradeSide.Sell && candle.Open < fill) fill = candle.Open;
        }
        // Final clamp into candle range so impossible prints can never leak into PnL.
        if (fill < candle.Low) fill = candle.Low;
        if (fill > candle.High) fill = candle.High;
        return fill;
    }

    private static decimal TrOverPrice(Candle c)
    {
        if (c.Close <= 0) return 0m;
        var range = c.High - c.Low;
        return range > 0 ? range / c.Close : 0m;
    }

    /// <summary>
    /// Real-data HTF gate: compares EMA20 + EMA50 slope on native HTF candles.
    /// Buys require rising EMA20/EMA50 with EMA20 > EMA50, sells require the inverse.
    /// Passes if HTF data was not provided. Advances _htfCursor for O(1) lookups as time flows.
    /// </summary>
    private bool PassesHtfMtfGate(DateTime nowUtc, TradeSide side)
    {
        if (CurrentHtfCandles is null || CurrentHtfCandles.Count < 55) return true;
        // Advance cursor to the last HTF candle with Timestamp <= nowUtc.
        while (CurrentHtfCursor + 1 < CurrentHtfCandles.Count && CurrentHtfCandles[CurrentHtfCursor + 1].Timestamp <= nowUtc)
            CurrentHtfCursor++;
        if (CurrentHtfCursor < 50) return true;

        var closes = new List<double>(CurrentHtfCursor + 1);
        for (var i = 0; i <= CurrentHtfCursor; i++)
        {
            closes.Add((double)CurrentHtfCandles[i].Close);
        }

        if (!TryComputeEma(closes, 20, out var ema20Series) ||
            !TryComputeEma(closes, 50, out var ema50Series) ||
            ema20Series.Count < 2 ||
            ema50Series.Count < 2)
        {
            return true;
        }

        var ema20Now = ema20Series[^1];
        var ema20Prev = ema20Series[^2];
        var ema50Now = ema50Series[^1];
        var ema50Prev = ema50Series[^2];

        var bullishBias = ema20Now > ema50Now && ema20Now > ema20Prev && ema50Now > ema50Prev;
        var bearishBias = ema20Now < ema50Now && ema20Now < ema20Prev && ema50Now < ema50Prev;

        if (!bullishBias && !bearishBias)
        {
            return true;
        }

        return side == TradeSide.Buy ? bullishBias : bearishBias;
    }

    /// <summary>
    /// Pullback filter: true if price is within 0.5 ATR of EMA21. Rejects entries that chase
    /// extended moves away from the mean — these are the lowest-R:R trades in a trending regime.
    /// Returns true if indicators are missing (don't block). Falls back to indicator EMA20
    /// when we cannot derive EMA21 from the current window.
    /// </summary>
    private static bool PassesPullbackFilter(decimal price, IndicatorSnapshot? ind, IReadOnlyList<Candle> window, decimal atrMultiplier)
    {
        if (ind?.ATR is not double atr || atr <= 0)
        {
            return true;
        }

        var ema21 = TryComputeEma(window.Select(c => (double)c.Close).ToList(), 21, out var ema21Series) && ema21Series.Count > 0
            ? ema21Series[^1]
            : ind.EMA20;

        if (ema21 is not double ema || ema <= 0)
        {
            return true;
        }

        var distance = Math.Abs((double)price - ema);
        var clampedMultiplier = Math.Clamp((double)atrMultiplier, 0.10d, 2.00d);
        return distance <= clampedMultiplier * atr;
    }

    private static bool TryComputeEma(IReadOnlyList<double> values, int period, out List<double> emaSeries)
    {
        emaSeries = new List<double>();
        if (values.Count < period || period <= 1)
        {
            return false;
        }

        var seed = values.Take(period).Average();
        emaSeries.Add(seed);

        var alpha = 2d / (period + 1d);
        var ema = seed;
        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] * alpha) + (ema * (1d - alpha));
            emaSeries.Add(ema);
        }

        return emaSeries.Count > 0;
    }

    /// <summary>
    /// MTF confluence check: returns true if the higher-timeframe (4h) trend aligns with the
    /// intended trade side. Aggregates 1h input into 4h buckets and compares a 10-period
    /// linear slope of closes. Ranging regime bypasses (mean-reversion trades are inherently
    /// contra-trend). Returns true if we don't have enough history (don't block early).
    /// </summary>
    private static bool PassesMtfConfluence(IReadOnlyList<Candle> window, TradeSide side)
    {
        if (window.Count < 40) return true;
        // Aggregate close prices into 4-bar buckets (1h → 4h) using last-close-per-bucket.
        var h4Closes = new List<decimal>(window.Count / 4);
        for (int i = 0; i < window.Count; i += 4)
        {
            int last = Math.Min(i + 3, window.Count - 1);
            h4Closes.Add(window[last].Close);
        }
        if (h4Closes.Count < 10) return true;
        var recent = h4Closes.Skip(h4Closes.Count - 10).ToList();
        // Simple slope: average of last 3 minus average of first 3 — positive = uptrend.
        decimal head = (recent[0] + recent[1] + recent[2]) / 3m;
        decimal tail = (recent[^1] + recent[^2] + recent[^3]) / 3m;
        decimal slope = tail - head;
        // Require at least 0.2% move across the window to count as a real trend (filters chop).
        var threshold = head * 0.002m;
        if (Math.Abs(slope) < threshold) return true; // flat 4h → allow both sides
        bool isUp = slope > 0;
        return side == TradeSide.Buy ? isUp : !isUp;
    }

    private TradeDecision ApplyStrategyRouter(
        Signal signal,
        TradeDecision decision,
        MarketRegimeDetector.RegimeResult regime,
        Candle candle)
    {
        if (_strategyRouters.Length == 0)
        {
            return decision;
        }

        var market = signal.MarketType == MarketType.Crypto ? "Crypto" : signal.MarketType.ToString();
        var router = _strategyRouters
            .OrderByDescending(r => r.EntryThreshold)
            .FirstOrDefault(r => r.CanHandle(market, signal, regime));
        if (router is null || decision.Confidence < router.EntryThreshold)
        {
            return decision;
        }

        var indicators = decision.Indicators ?? new IndicatorSnapshot { CurrentPrice = candle.Close };
        var atr = indicators.ATR is > 0 ? (decimal)indicators.ATR.Value : Math.Abs(candle.Close - decision.StopLoss);
        if (atr <= 0m)
        {
            atr = candle.Close * 0.005m;
        }

        var context = new StrategyRouterContext(
            signal,
            indicators,
            regime,
            ResolveBacktestExecutionProfile(signal),
            decision.Side,
            decision.Confidence,
            "Backtest parity router",
            "Router synchronized with live strategy family",
            decision.PositionSizePercent,
            candle.Close,
            atr,
            ResolveBacktestRoundTripCostPercent(signal),
            new List<string> { "Router gate accepted in backtest parity mode." });

        RequireRunContext().CoverageFlags |= DecisionCoverageFlags.StrategyRouter;
        var routed = router.BuildDecision(context);
        return decision with
        {
            StrategyTag = routed.StrategyTag,
            ExitPolicyTag = routed.ExitPolicyTag,
            RegimeTag = routed.RegimeTag,
            Reasoning = $"{decision.Reasoning} | Router={routed.StrategyTag}"
        };
    }

    private TradeDecision ApplyEdgeScoreOverlay(Signal signal, TradeDecision decision)
    {
        if (_edgeScoreService is null || !decision.ShouldTrade)
        {
            return decision;
        }

        var guards = new EvaluationGuards(
            new ExpectancyGuardResult(true, 1.00m, "Backtest parity baseline", 10, 0m, 1.0),
            new SymbolGuardResult(true, 1.00m, "Backtest parity baseline"),
            new PromotionGuardResult(true, 1.00m, "Backtest parity baseline", true, 0.0, "Backtest"),
            (true, "Backtest parity baseline"),
            (true, "Backtest parity baseline", decision.PositionSizePercent));

        var breakdown = _edgeScoreService.Evaluate(
            signal,
            decision,
            guards,
            new EdgeScoreEvaluationOptions(EdgePipelineKind.Backtest, 0.85m));
        RequireRunContext().CoverageFlags |= DecisionCoverageFlags.EdgeScore;

        var effectiveMultiplier = breakdown.RecommendedSizeMultiplier <= 0m
            ? 0.85m
            : Math.Clamp(breakdown.RecommendedSizeMultiplier, 0.85m, 1.15m);
        var scaledSize = Math.Clamp(decision.PositionSizePercent * effectiveMultiplier, 0.0005m, 1.00m);
        if (!breakdown.Approved)
        {
            return decision with
            {
                ShouldTrade = false,
                Reasoning = $"{decision.Reasoning} | EdgeScore {breakdown.AdjustedEdgeScore:F1} ({breakdown.DecisionBand}) blocked"
            };
        }

        return decision with
        {
            PositionSizePercent = scaledSize,
            Reasoning = $"{decision.Reasoning} | EdgeScore {breakdown.AdjustedEdgeScore:F1} ({breakdown.DecisionBand}) x{effectiveMultiplier:F2}"
        };
    }

    private MarketExecutionProfile ResolveBacktestExecutionProfile(Signal signal)
    {
        var market = signal.MarketType == MarketType.Crypto ? "Crypto" : signal.MarketType.ToString();
        var spreadPercent = CurrentCosts.SpreadBps / 100m;
        var slippagePercent = CurrentCosts.SlippageBps / 100m;
        var commissionPercent = CurrentCosts.CommissionBps / 100m;
        return new MarketExecutionProfile(market, "Backtest", slippagePercent, spreadPercent, commissionPercent, 0m, 150);
    }

    private decimal ResolveBacktestRoundTripCostPercent(Signal signal)
    {
        var profile = ResolveBacktestExecutionProfile(signal);
        return profile.SpreadPercent + (profile.SlippagePercent * 2m) + (profile.CommissionPercentPerSide * 2m);
    }

    private TradeDecision ApplyInferenceOverlay(IReadOnlyList<Candle> window, TradeDecision decision)
    {
        if (_inferenceAgent is null || window.Count < 6)
        {
            return decision;
        }

        RequireRunContext().CoverageFlags |= DecisionCoverageFlags.Inference;
        var prediction = PredictWithFeatureAgent(window);
        if (!prediction.IsValid)
        {
            return decision with { Reasoning = $"{decision.Reasoning} | ML inference unavailable (model/pool invalid)." };
        }
        RequireRunContext().InferencePredictionValid = true;

        var confidenceDelta = prediction.Value switch
        {
            > 0.0025f => 4,
            > 0.0008f => 2,
            < -0.0025f => -4,
            < -0.0008f => -2,
            _ => 0
        };

        var adjustedConfidence = Math.Clamp(decision.Confidence + confidenceDelta, 0, 100);
        return decision with
        {
            Confidence = adjustedConfidence,
            Reasoning = $"{decision.Reasoning} | ML pred={prediction.Value:F4} conf={prediction.Confidence:F2}"
        };
    }

    private PredictionResult PredictWithFeatureAgent(IReadOnlyList<Candle> window)
    {
        var featureAgent = _featureEngineeringAgent ?? new FeatureEngineeringAgent();
        foreach (var candle in window)
        {
            featureAgent.ProcessTickNatively((double)candle.Close);
        }

        return _inferenceAgent!.PredictNextReturn(featureAgent);
    }

    private static bool TryBuildFallbackDecision(
        TradeDecision decision,
        Candle candle,
        MarketRegimeDetector.RegimeResult regime,
        int minConfidence,
        out TradeDecision fallback)
    {
        fallback = decision;
        if (!decision.Reasoning.Contains("Tradeability gate failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var indicators = decision.Indicators;
        var atr = indicators?.ATR is > 0
            ? (decimal)indicators.ATR.Value
            : Math.Max(candle.Close * 0.004m, Math.Abs(candle.High - candle.Low));
        if (atr <= 0m)
        {
            atr = Math.Max(0.25m, candle.Close * 0.0025m);
        }

        var bias = (indicators?.EMA20, indicators?.EMA50) switch
        {
            (double ema20, double ema50) when ema20 > ema50 => TradeSide.Buy,
            (double ema20, double ema50) when ema20 < ema50 => TradeSide.Sell,
            _ => candle.Close >= candle.Open ? TradeSide.Buy : TradeSide.Sell
        };

        var stopDistance = atr * 1.20m;
        var takeDistance = atr * 2.00m;
        var stopLoss = bias == TradeSide.Buy ? candle.Close - stopDistance : candle.Close + stopDistance;
        var takeProfit = bias == TradeSide.Buy ? candle.Close + takeDistance : candle.Close - takeDistance;
        if (stopLoss <= 0m || takeProfit <= 0m)
        {
            return false;
        }

        fallback = decision with
        {
            ShouldTrade = true,
            Side = bias,
            Confidence = Math.Max(minConfidence + 2, decision.Confidence),
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            PositionSizePercent = Math.Clamp(decision.PositionSizePercent <= 0m ? 0.035m : decision.PositionSizePercent, 0.02m, 0.06m),
            StrategyTag = $"Fallback{regime.Regime}",
            ExitPolicyTag = "FallbackBalanced",
            Reasoning = $"{decision.Reasoning} | Fallback activation (tradeability soft-pass)"
        };
        return true;
    }

    private static TradeDecision NormalizeDecisionRiskReward(TradeDecision decision, decimal referencePrice)
    {
        if (!decision.ShouldTrade || referencePrice <= 0m || decision.StopLoss <= 0m || decision.TakeProfit <= 0m)
        {
            return decision;
        }

        var risk = decision.Side == TradeSide.Buy
            ? referencePrice - decision.StopLoss
            : decision.StopLoss - referencePrice;
        if (risk <= 0m)
        {
            return decision;
        }

        var reward = decision.Side == TradeSide.Buy
            ? decision.TakeProfit - referencePrice
            : referencePrice - decision.TakeProfit;
        if (reward <= 0m)
        {
            reward = risk * 1.20m;
        }

        var rr = reward / risk;
        // Floor only — never cap upside. RR floor raised from 1.50 → 2.00 because with OKX
        // round-trip costs (~15-20bps taker+spread+slip), a 1.50 gross RR nets ~1.30 after costs.
        // At 50% WR that's still positive but marginal. 2.0 gross → ~1.70 net → robust edge.
        const decimal minTargetRr = 2.00m;
        if (rr >= minTargetRr)
        {
            return decision;
        }

        var normalizedReward = risk * minTargetRr;
        var takeProfit = decision.Side == TradeSide.Buy
            ? referencePrice + normalizedReward
            : referencePrice - normalizedReward;

        return decision with
        {
            TakeProfit = takeProfit,
            Reasoning = $"{decision.Reasoning} | RR floor lifted to {minTargetRr:F2}"
        };
    }

    private static BacktestResult BuildFinalResult(
        string sym,
        IReadOnlyList<Candle> candles,
        List<BacktestTradeResult> trades,
        BacktestState state,
        decimal start,
        DecisionCoverageFlags coverageFlags,
        bool inferenceModelAvailable,
        bool inferencePredictionValid,
        IReadOnlyDictionary<string, int>? rejectReasons = null)
    {
        var profits = trades.Where(t => t.PnL > 0).Select(t => t.PnL).ToList();
        var losses = trades.Where(t => t.PnL <= 0).Select(t => t.PnL).ToList();
        var equityCurve = BuildEquityCurve(start, trades);

        // ── Pillar 4: Portfolio-level bucketing ──
        var regimeBuckets = BuildBuckets(trades, t => t.RegimeTag);
        var exitPolicyBuckets = BuildBuckets(trades, t => t.ExitPolicyTag);
        var quarterlyBuckets = BuildBuckets(trades, t =>
        {
            var q = (t.ExitTime.Month - 1) / 3 + 1;
            return $"{t.ExitTime.Year}-Q{q}";
        });

        var totalFees = trades.Sum(t => t.TotalFeesPaid);
        var totalFunding = trades.Sum(t => t.NetFunding);
        var netPnL = state.Equity - start;
        var grossPnL = netPnL + totalFees - totalFunding;
        var costLeakage = grossPnL != 0m ? (double)(totalFees / Math.Abs(grossPnL)) * 100.0 : 0.0;

        var topRejects = rejectReasons is null
            ? new List<string>()
            : rejectReasons.OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key}({kv.Value})")
                .ToList();

        return new BacktestResult 
        { 
            Symbol = sym, TotalTrades = trades.Count, TotalPnL = netPnL, 
            FinalEquity = state.Equity, MaxDrawdownPercent = state.MaxDDPercent, Trades = trades,
            WinningTrades = trades.Count(t => t.PnL > 0),
            LosingTrades = trades.Count(t => t.PnL <= 0),
            StartingCapital = start,
            ProfitFactor = losses.Count == 0 ? (profits.Count > 0 ? 9.99 : 0.0) : (double)(profits.Sum() / Math.Abs(losses.Sum())),
            SharpeRatio = ComputeSharpeRatioFromTrades(trades),
            SortinoRatio = ComputeSortinoRatioFromTrades(trades),
            AverageRR = ComputeAverageRiskReward(trades),
            MaxConsecutiveLosses = ComputeMaxConsecutiveLosses(trades),
            StartDate = candles.Count > 0 ? candles[0].Timestamp : default,
            EndDate = candles.Count > 0 ? candles[^1].Timestamp : default,
            EquityCurve = equityCurve,
            CoverageFlags = coverageFlags,
            CoverageSummary = coverageFlags.ToString(),
            EntryRejectReasons = rejectReasons is null
                ? new Dictionary<string, int>()
                : new Dictionary<string, int>(rejectReasons),
            InferenceModelAvailable = inferenceModelAvailable,
            InferencePredictionValid = inferencePredictionValid,
            RegimeBuckets = regimeBuckets,
            ExitPolicyBuckets = exitPolicyBuckets,
            QuarterlyBuckets = quarterlyBuckets,
            TopRejectReasons = topRejects,
            TotalFeesPaid = totalFees,
            TotalNetFunding = totalFunding,
            GrossPnL = grossPnL,
            CostLeakagePercent = costLeakage
        };
    }

    private static Dictionary<string, BucketPerformance> BuildBuckets(
        IReadOnlyList<BacktestTradeResult> trades,
        Func<BacktestTradeResult, string> keySelector)
    {
        var result = new Dictionary<string, BucketPerformance>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in trades.GroupBy(keySelector))
        {
            var key = string.IsNullOrWhiteSpace(group.Key) ? "Unknown" : group.Key;
            var groupTrades = group.ToList();
            var winners = groupTrades.Where(t => t.PnL > 0).ToList();
            var losers = groupTrades.Where(t => t.PnL <= 0).ToList();
            var avgWin = winners.Count > 0 ? winners.Average(t => (double)t.PnL) : 0.0;
            var avgLoss = losers.Count > 0 ? losers.Average(t => Math.Abs((double)t.PnL)) : 0.0;
            var rr = avgLoss > 0 ? avgWin / avgLoss : 0.0;
            var pf = losers.Count > 0 && losers.Sum(t => t.PnL) != 0
                ? (double)(winners.Sum(t => t.PnL) / Math.Abs(losers.Sum(t => t.PnL)))
                : (winners.Count > 0 ? 9.99 : 0.0);

            result[key] = new BucketPerformance
            {
                TradeCount = groupTrades.Count,
                Winners = winners.Count,
                TotalPnL = groupTrades.Sum(t => t.PnL),
                AverageRR = rr,
                ProfitFactor = pf
            };
        }
        return result;
    }

    private PaperPosition? RejectEntry(string reason)
    {
        var rejects = RequireRunContext().EntryRejectCounts;
        if (rejects.TryGetValue(reason, out var count))
        {
            rejects[reason] = count + 1;
        }
        else
        {
            rejects[reason] = 1;
        }

        return null;
    }

    private static string BuildShouldTradeRejectReason(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return "ShouldTradeFalse";
        }

        var normalized = reasoning.Trim();
        var separators = new[] { '|', ':', ';', '.', ',' };
        var firstCut = normalized.IndexOfAny(separators);
        if (firstCut > 0)
        {
            normalized = normalized[..firstCut];
        }

        normalized = normalized.Trim();
        if (normalized.Length > 40)
        {
            normalized = normalized[..40];
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "ShouldTradeFalse";
        }

        return $"ShouldTradeFalse:{normalized}";
    }

    private void ForceCloseOpenPositionIfNeeded(
        IReadOnlyList<Candle> candles,
        PaperPosition? openPosition,
        BacktestState state,
        List<BacktestTradeResult> trades,
        List<Trade> fullTradeHistory)
    {
        if (openPosition == null || candles.Count == 0)
        {
            return;
        }

        var lastCandle = candles[^1];
        var exitPrice = ApplySlippage(lastCandle.Close, openPosition.Side, isEntry: false, TrOverPrice(lastCandle));
        var remainingQty = openPosition.RemainingQuantity > 0m ? openPosition.RemainingQuantity : openPosition.Quantity;
        var feesPortion = CalculateRoundTripFees(openPosition.EntryPrice, exitPrice, remainingQty, openPosition.IsMakerEntry);
        var notional = remainingQty * ((openPosition.EntryPrice + exitPrice) / 2m);
        var funding = _fundingRateProvider is not null and not NullFundingRateProvider
            ? FundingAccrualService.CalculateFromProvider(openPosition.Symbol, notional, openPosition.Side, openPosition.EntryTime, lastCandle.Timestamp, _fundingRateProvider)
            : FundingAccrualService.Calculate(notional, openPosition.Side, openPosition.EntryTime, lastCandle.Timestamp, CurrentCosts.FundingBpsPer8h);
        var netPnL = CalculateNetPnl(openPosition.EntryPrice, exitPrice, remainingQty, openPosition.Side, openPosition.IsMakerEntry) + funding;
        state.Update(netPnL);
        state.RegisterTradeResult(netPnL, CurrentTimeframe);
        state.ReleaseMargin(openPosition.InitialMargin, remainingQty, openPosition.Quantity);

        trades.Add(new BacktestTradeResult
        {
            EntryTime = openPosition.EntryTime,
            ExitTime = lastCandle.Timestamp,
            Side = openPosition.Side,
            PnL = netPnL,
            ExitReason = "EndOfData",
            EntryPrice = openPosition.EntryPrice,
            ExitPrice = exitPrice,
            PnLPercent = CalculatePnLPercent(openPosition.EntryPrice, exitPrice, openPosition.Side),
            Reasoning = openPosition.Reasoning,
            Confidence = openPosition.Confidence,
            StrategyTag = openPosition.StrategyTag,
            RegimeTag = openPosition.RegimeTag,
            ExitPolicyTag = openPosition.ExitPolicyTag,
            TotalFeesPaid = feesPortion,
            NetFunding = funding
        });

        fullTradeHistory.Add(MapClosedPosition(openPosition, lastCandle.Timestamp, exitPrice, feesPortion, funding));
    }

    private static Trade MapClosedPosition(PaperPosition position, DateTime exitTime, decimal exitPrice, decimal totalFeesPaid, decimal netFunding) => new()
    {
        Symbol = position.Symbol,
        Side = position.Side,
        EntryPrice = position.EntryPrice,
        ExitPrice = exitPrice,
        Quantity = position.Quantity,
        EntryTime = position.EntryTime,
        ExitTime = exitTime,
        StrategyTag = position.StrategyTag,
        RegimeTag = position.RegimeTag,
        ExitPolicyTag = position.ExitPolicyTag,
        Status = TradeStatus.Executed,
        PromotionStage = "Backtest",
        ExecutionMode = "SIMULATED",
        Leverage = position.Leverage <= 0m ? 1m : position.Leverage,
        MarginMode = position.MarginMode,
        ClosedQuantity = position.Quantity,
        AverageExitPrice = exitPrice,
        TotalFeesPaid = totalFeesPaid,
        NetFunding = netFunding,
        LifecycleStatus = TradeLifecycleStatus.Closed
    };

    private static List<decimal> BuildEquityCurve(decimal startingCapital, IReadOnlyList<BacktestTradeResult> trades)
    {
        var curve = new List<decimal>(trades.Count + 1) { startingCapital };
        var equity = startingCapital;
        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            equity += trade.PnL;
            curve.Add(equity);
        }

        return curve;
    }

    private static double ComputeSharpeRatioFromTrades(IReadOnlyList<BacktestTradeResult> trades)
    {
        if (trades.Count < 2)
        {
            return 0.0;
        }

        var returns = trades.Select(t => (double)t.PnL).ToList();
        var mean = returns.Average();
        var variance = returns.Sum(r => Math.Pow(r - mean, 2)) / (returns.Count - 1);
        var stdDev = Math.Sqrt(Math.Max(variance, 0));
        return stdDev > 0 ? mean / stdDev * Math.Sqrt(252) : 0.0;
    }

    private static double ComputeSortinoRatioFromTrades(IReadOnlyList<BacktestTradeResult> trades)
    {
        if (trades.Count < 2)
        {
            return 0.0;
        }

        var returns = trades.Select(t => (double)t.PnL).ToList();
        var mean = returns.Average();
        var downsideReturns = returns.Where(r => r < 0).ToList();
        if (downsideReturns.Count == 0)
        {
            return mean > 0 ? 9.99 : 0.0;
        }

        var downsideVariance = downsideReturns.Sum(r => r * r) / downsideReturns.Count;
        var downsideDeviation = Math.Sqrt(Math.Max(downsideVariance, 0));
        return downsideDeviation > 0 ? mean / downsideDeviation * Math.Sqrt(252) : 0.0;
    }

    private static double ComputeAverageRiskReward(IReadOnlyList<BacktestTradeResult> trades)
    {
        var winners = trades.Where(t => t.PnL > 0).Select(t => (double)t.PnL).ToList();
        var losers = trades.Where(t => t.PnL < 0).Select(t => Math.Abs((double)t.PnL)).ToList();
        if (winners.Count == 0 || losers.Count == 0)
        {
            return 0.0;
        }

        return winners.Average() / losers.Average();
    }

    private static int ComputeMaxConsecutiveLosses(IReadOnlyList<BacktestTradeResult> trades)
    {
        var current = 0;
        var max = 0;
        foreach (var trade in trades.OrderBy(t => t.ExitTime))
        {
            if (trade.PnL < 0)
            {
                current++;
                max = Math.Max(max, current);
            }
            else
            {
                current = 0;
            }
        }

        return max;
    }

    private class BacktestState 
    {
        public decimal Equity { get; private set; }
        public decimal PeakEquity { get; private set; }
        public decimal MaxDDPercent { get; private set; }
        public decimal ReservedMargin { get; private set; }
        public decimal AvailableEquity => Math.Max(0m, Equity - ReservedMargin);
        private readonly Queue<decimal> _recentTradePnls = new();
        private readonly Queue<(DateTime Timestamp, decimal Equity)> _equityHistory = new();
        private DateTime _haltNewEntriesUntilUtc = DateTime.MinValue;
        private int _cooldownBarsRemaining;
        public bool CanOpenTrade => _cooldownBarsRemaining <= 0;
        public bool ShouldBlockByCircuitBreaker(DateTime nowUtc)
        {
            if (_haltNewEntriesUntilUtc > nowUtc)
            {
                return true;
            }

            var lookbackStart = nowUtc.AddDays(-7);
            while (_equityHistory.Count > 0 && _equityHistory.Peek().Timestamp < lookbackStart)
            {
                _equityHistory.Dequeue();
            }

            if (_equityHistory.Count == 0)
            {
                return false;
            }

            var peak = _equityHistory.Max(p => p.Equity);
            if (peak <= 0m)
            {
                return false;
            }

            var rollingDrawdownPercent = ((peak - Equity) / peak) * 100m;
            if (rollingDrawdownPercent <= 2m)
            {
                return false;
            }

            _haltNewEntriesUntilUtc = nowUtc.AddHours(24);
            return true;
        }


        public BacktestState(decimal initial) { Equity = initial; PeakEquity = initial; }

        public void Update(decimal pnl) 
        {
            Equity += pnl;
            if (Equity > PeakEquity) PeakEquity = Equity;
            decimal dd = PeakEquity > 0 ? (PeakEquity - Equity) / PeakEquity * 100 : 0;
            if (dd > MaxDDPercent) MaxDDPercent = dd;
        }

        public bool TryReserveMargin(decimal margin)
        {
            if (margin <= 0m) return false;
            if (margin > AvailableEquity) return false;
            ReservedMargin += margin;
            return true;
        }

        public void ReleaseMargin(decimal initialMargin, decimal releasedQty, decimal totalQty)
        {
            if (initialMargin <= 0m || releasedQty <= 0m || totalQty <= 0m) return;
            var ratio = Math.Min(1m, releasedQty / totalQty);
            var release = initialMargin * ratio;
            ReservedMargin = Math.Max(0m, ReservedMargin - release);
        }

        public void RegisterTradeResult(decimal pnl, string timeframe)
        {
            _recentTradePnls.Enqueue(pnl);
            while (_recentTradePnls.Count > 40)
            {
                _recentTradePnls.Dequeue();
            }

            if (_recentTradePnls.Count < 20)
            {
                return;
            }

            var snapshot = _recentTradePnls.ToArray();
            var avg = snapshot.Average();
            var losses = snapshot.Count(x => x < 0m);
            var wins = snapshot.Count(x => x > 0m);
            if (avg >= 0m || losses <= wins)
            {
                return;
            }

            _cooldownBarsRemaining = timeframe switch
            {
                "1m" => 120,
                "5m" => 72,
                "15m" => 36,
                "4h" => 8,
                "1d" => 3,
                _ => 24
            };
        }

        public bool ShouldBlockByExpectancy(BacktestGuardOptions options, BacktestGuardRiskProfile profile)
        {
            if (!options.EnableExpectancyGuard)
            {
                return false;
            }

            var window = Math.Max(5, profile.ExpectancyWindowTrades > 0 ? profile.ExpectancyWindowTrades : options.ExpectancyWindowTrades);
            if (_recentTradePnls.Count < window)
            {
                return false;
            }

            var slice = _recentTradePnls.TakeLast(window).ToArray();
            var expectancy = slice.Average();
            var minExpectancy = profile.MinExpectancyPerTrade != 0m
                ? profile.MinExpectancyPerTrade
                : options.MinExpectancyPerTrade;
            if (expectancy >= minExpectancy)
            {
                return false;
            }

            var cooldownBars = profile.ExpectancyCooldownBars > 0
                ? profile.ExpectancyCooldownBars
                : options.ExpectancyCooldownBars;
            _cooldownBarsRemaining = Math.Max(_cooldownBarsRemaining, Math.Max(1, cooldownBars));
            return true;
        }

        public void AdvanceBar(DateTime nowUtc)
        {
            _equityHistory.Enqueue((nowUtc, Equity));
            var retentionStart = nowUtc.AddDays(-8);
            while (_equityHistory.Count > 0 && _equityHistory.Peek().Timestamp < retentionStart)
            {
                _equityHistory.Dequeue();
            }

            if (_cooldownBarsRemaining > 0)
            {
                _cooldownBarsRemaining--;
            }
        }
    }

    private BacktestGuardRiskProfile ResolveGuardProfile() => CurrentRiskProfile switch
    {
        "Low" => _guardOptions.LowRisk,
        "Aggressive" => _guardOptions.AggressiveRisk,
        _ => _guardOptions.MediumRisk
    };

    private (int ConfidencePenalty, decimal PositionSizeMultiplier) ResolveRegimePenalty(MarketRegime regime)
    {
        if (!_guardOptions.EnableRegimeTimeframeVeto)
        {
            return (0, 1m);
        }

        var profile = ResolveGuardProfile();
        var shouldApplyPenalty =
            (CurrentTimeframe == "1h" && _guardOptions.BlockVolatileOnOneHour && regime == MarketRegime.Volatile) ||
            (CurrentTimeframe == "4h" && _guardOptions.BlockVolatileOnFourHour && regime == MarketRegime.Volatile) ||
            (CurrentTimeframe == "1m" && _guardOptions.BlockRangingOnOneMinute && regime == MarketRegime.Ranging);

        if (!shouldApplyPenalty)
        {
            return (0, 1m);
        }

        var multiplier = Math.Clamp(profile.PositionSizeMultiplierOnVetoRegime, 0.10m, 1m);
        var penalty = Math.Max(0, profile.ConfidencePenaltyOnVetoRegime);
        return (penalty, multiplier);
    }
}
