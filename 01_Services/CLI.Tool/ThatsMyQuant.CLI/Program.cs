using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure.Services;
using ThatsMyQuant.Infrastructure.Services.Strategy;
using ThatsMyQuant.Infrastructure.Services.Foundation;
using ThatsMyQuant.Infrastructure.Services.Patterns;
using ThatsMyQuant.Infrastructure.Services.Risk;
using ThatsMyQuant.Infrastructure.Services.MetaLearning;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

switch (args[0].ToLowerInvariant())
{
    case "train":
        return RunTrain(args);
    case "evaluate":
        return RunEvaluate(args);
    case "drift-check":
        return RunDriftCheck(args);
    case "fetch-data":
        return await RunFetchData(args);
    case "backtest":
        return await RunBacktest(args);
    case "walk-forward":
        return await RunWalkForward(args);
    case "validate":
        return await RunValidate(args);
    case "list-results":
        return RunListResults(args);
    default:
        Console.WriteLine($"Unknown command: {args[0]}");
        PrintUsage();
        return 1;
}

// ========== Helper: Build full 5-tier agent coordinator ==========

AgentCoordinator BuildFullCoordinator()
{
    // Tier 1: Foundation agents
    IFoundationAgent[] foundation =
    [
        new MicrostructureAgent(loggerFactory.CreateLogger<MicrostructureAgent>()),
        new VolumeProfileAgent(loggerFactory.CreateLogger<VolumeProfileAgent>()),
    ];

    // Tier 2: Pattern agents
    IPatternAgent[] patterns =
    [
        new BollingerBandAgent(loggerFactory.CreateLogger<BollingerBandAgent>()),
        new IchimokuAgent(loggerFactory.CreateLogger<IchimokuAgent>()),
        new FibonacciAgent(loggerFactory.CreateLogger<FibonacciAgent>()),
        new VwapDeviationAgent(loggerFactory.CreateLogger<VwapDeviationAgent>()),
    ];

    // Tier 3: Strategy agents (the core edge generators)
    IStrategyAgent[] strategies =
    [
        new MomentumStrategyAgent(loggerFactory.CreateLogger<MomentumStrategyAgent>()),
        new BreakoutStrategyAgent(loggerFactory.CreateLogger<BreakoutStrategyAgent>()),
        new ScalpingStrategyAgent(loggerFactory.CreateLogger<ScalpingStrategyAgent>()),
        new StatisticalArbitrageAgent(loggerFactory.CreateLogger<StatisticalArbitrageAgent>()),
        new VolatilityExpansionAgent(loggerFactory.CreateLogger<VolatilityExpansionAgent>()),
    ];

    // Tier 4: Risk guards
    IRiskGuardAgent[] riskGuards =
    [
        new KellyPositionSizerAgent(loggerFactory.CreateLogger<KellyPositionSizerAgent>()),
        new PortfolioOptimiserAgent(loggerFactory.CreateLogger<PortfolioOptimiserAgent>()),
        new CorrelationGuardAgent(loggerFactory.CreateLogger<CorrelationGuardAgent>()),
    ];

    // Tier 5: Meta-learning (skip DB-dependent agents for CLI; use regime switching only)
    IMetaLearningAgent[] meta =
    [
        new RegimeSwitchingModelAgent(loggerFactory.CreateLogger<RegimeSwitchingModelAgent>()),
    ];

    return new AgentCoordinator(foundation, patterns, strategies, riskGuards, meta,
        loggerFactory.CreateLogger<AgentCoordinator>());
}

// ========== Helper: Fetch or load candles ==========

async Task<(List<Candle> candles, string symbol)> ResolveCandles(string source, string interval, int totalCandles)
{
    if (File.Exists(source))
    {
        var candles = BinanceHistoricalDataService.LoadFromCsv(source);
        string symbol = Path.GetFileNameWithoutExtension(source).Split('_')[0].ToUpperInvariant();
        Console.WriteLine($"  Loaded {candles.Count} candles from CSV: {source}");
        return (candles, symbol);
    }
    else
    {
        string symbol = source.ToUpperInvariant();
        Console.WriteLine($"  Fetching {totalCandles} {interval} candles for {symbol} from Binance...");
        using var http = new HttpClient();
        var fetcher = new BinanceHistoricalDataService(http,
            loggerFactory.CreateLogger<BinanceHistoricalDataService>());
        var candles = await fetcher.FetchCandlesAsync(symbol, interval, totalCandles).ConfigureAwait(false);
        return (candles, symbol);
    }
}

// ========== Commands ==========

int RunTrain(string[] args)
{
    string dataPath = args.Length > 1 ? args[1] : "training_candles.csv";
    string modelPath = args.Length > 2 ? args[2] : "PredictiveAlphaModel.zip";

    Console.WriteLine($"[Train] Loading candle data from: {dataPath}");

    if (!File.Exists(dataPath))
    {
        Console.WriteLine($"[Train] ERROR: Data file not found: {dataPath}");
        Console.WriteLine("[Train] Expected CSV format: Timestamp,Open,High,Low,Close,Volume");
        return 1;
    }

    var candles = BinanceHistoricalDataService.LoadFromCsv(dataPath);
    if (candles.Count < 100)
    {
        Console.WriteLine($"[Train] ERROR: Insufficient data ({candles.Count} candles). Need at least 100.");
        return 1;
    }

    Console.WriteLine($"[Train] Loaded {candles.Count} candles. Building feature set...");
    var trainingSet = FeatureSetBuilder.BuildTrainingSet(candles);
    Console.WriteLine($"[Train] Feature set: {trainingSet.Count} samples with 5 features each.");

    var trainer = new AlphaModelTrainer(loggerFactory.CreateLogger<AlphaModelTrainer>());

    Console.WriteLine($"[Train] Training FastTree model...");
    double rSquared = trainer.TrainAndSaveModel(trainingSet, modelPath);

    Console.WriteLine($"[Train] Model saved to: {modelPath}");
    Console.WriteLine($"[Train] R-Squared: {rSquared:F4}");
    Console.WriteLine(rSquared > 0.5 ? "[Train] Model quality: GOOD" : rSquared > 0.2 ? "[Train] Model quality: ACCEPTABLE" : "[Train] Model quality: POOR \u2014 consider more/better data");

    return 0;
}

int RunEvaluate(string[] args)
{
    string modelPath = args.Length > 1 ? args[1] : "PredictiveAlphaModel.zip";
    string dataPath = args.Length > 2 ? args[2] : "evaluation_candles.csv";

    if (!File.Exists(modelPath))
    {
        Console.WriteLine($"[Evaluate] ERROR: Model not found: {modelPath}");
        return 1;
    }

    if (!File.Exists(dataPath))
    {
        Console.WriteLine($"[Evaluate] ERROR: Evaluation data not found: {dataPath}");
        return 1;
    }

    Console.WriteLine($"[Evaluate] Loading model from: {modelPath}");
    var agent = new InferenceAgent(modelPath);

    var candles = BinanceHistoricalDataService.LoadFromCsv(dataPath);
    var evalSet = FeatureSetBuilder.BuildTrainingSet(candles);
    Console.WriteLine($"[Evaluate] Running inference on {evalSet.Count} samples...");

    int valid = 0, invalid = 0;
    double sumSquaredError = 0;
    double sumAbsError = 0;

    foreach (var sample in evalSet)
    {
        var result = agent.PredictNextReturn(sample.Features);
        if (result.IsValid)
        {
            valid++;
            double error = result.Value - sample.ForwardReturn;
            sumSquaredError += error * error;
            sumAbsError += Math.Abs(error);
        }
        else
        {
            invalid++;
        }
    }

    if (valid > 0)
    {
        double rmse = Math.Sqrt(sumSquaredError / valid);
        double mae = sumAbsError / valid;
        Console.WriteLine($"[Evaluate] Valid predictions: {valid}/{valid + invalid}");
        Console.WriteLine($"[Evaluate] RMSE: {rmse:F4}");
        Console.WriteLine($"[Evaluate] MAE:  {mae:F4}");
    }
    else
    {
        Console.WriteLine("[Evaluate] No valid predictions \u2014 model may not be loaded correctly.");
    }

    return 0;
}

int RunDriftCheck(string[] args)
{
    string baselinePath = args.Length > 1 ? args[1] : "training_candles.csv";
    string livePath = args.Length > 2 ? args[2] : "live_candles.csv";

    if (!File.Exists(baselinePath) || !File.Exists(livePath))
    {
        Console.WriteLine("[DriftCheck] ERROR: Need both baseline and live data files.");
        return 1;
    }

    var baselineCandles = BinanceHistoricalDataService.LoadFromCsv(baselinePath);
    var liveCandles = BinanceHistoricalDataService.LoadFromCsv(livePath);

    var baselineFeatures = FeatureSetBuilder.BuildTrainingSet(baselineCandles).Select(s => s.Features).ToList();
    var liveFeatures = FeatureSetBuilder.BuildTrainingSet(liveCandles).Select(s => s.Features).ToList();

    if (baselineFeatures.Count < 10 || liveFeatures.Count < 10)
    {
        Console.WriteLine("[DriftCheck] ERROR: Need at least 10 samples in each dataset.");
        return 1;
    }

    var monitor = new FeatureDriftMonitor(loggerFactory.CreateLogger<FeatureDriftMonitor>(), featureDimensions: 5);
    monitor.SetBaselineFromData(baselineFeatures);

    Console.WriteLine($"[DriftCheck] Baseline: {baselineFeatures.Count} samples. Live: {liveFeatures.Count} samples.");
    Console.WriteLine("[DriftCheck] Running Page-Hinkley change detection...");

    foreach (var vec in liveFeatures)
        monitor.Observe(vec);

    var report = monitor.GetDriftReport();

    Console.WriteLine();
    Console.WriteLine($"Overall: {(report.OverallHealthy ? "HEALTHY" : "DRIFT DETECTED")} ({report.DriftedFeatureCount}/{report.Features.Count} features drifted)");
    Console.WriteLine();

    foreach (var f in report.Features)
    {
        string status = f.IsDrifted ? "DRIFTED" : "OK";
        Console.WriteLine($"  [{status,-7}] {f.FeatureName,-25} PH={f.PageHinkleyStat:F2}  Baseline \u03bc={f.BaselineMean:F4} \u03c3={f.BaselineStddev:F4}  Live \u03bc={f.LiveMean:F4} \u03c3={f.LiveStddev:F4}");
    }

    return report.OverallHealthy ? 0 : 2;
}

async Task<int> RunFetchData(string[] args)
{
    string symbol = args.Length > 1 ? args[1].ToUpperInvariant() : "BTCUSDT";
    string interval = args.Length > 2 ? args[2] : "1h";
    int totalCandles = args.Length > 3 && int.TryParse(args[3], out var tc) ? tc : 2000;
    string outputPath = args.Length > 4 ? args[4] : $"{symbol}_{interval}_{totalCandles}.csv";

    Console.WriteLine($"[FetchData] Fetching {totalCandles} {interval} candles for {symbol} from Binance...");

    using var http = new HttpClient();
    var service = new BinanceHistoricalDataService(http,
        loggerFactory.CreateLogger<BinanceHistoricalDataService>());

    var candles = await service.FetchCandlesAsync(symbol, interval, totalCandles).ConfigureAwait(false);

    if (candles.Count == 0)
    {
        Console.WriteLine("[FetchData] ERROR: No candles returned. Check symbol and interval.");
        return 1;
    }

    await BinanceHistoricalDataService.SaveToCsvAsync(candles, outputPath).ConfigureAwait(false);
    Console.WriteLine($"[FetchData] Saved {candles.Count} candles to: {outputPath}");
    Console.WriteLine($"[FetchData] Range: {candles[0].Timestamp:yyyy-MM-dd HH:mm} \u2192 {candles[^1].Timestamp:yyyy-MM-dd HH:mm}");

    return 0;
}

async Task<int> RunBacktest(string[] args)
{
    string source = args.Length > 1 ? args[1] : "BTCUSDT";
    string interval = args.Length > 2 ? args[2] : "1h";
    int totalCandles = args.Length > 3 && int.TryParse(args[3], out var tc) ? tc : 2000;
    decimal capital = 100_000m;
    string tag = "";

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--capital" && i + 1 < args.Length && decimal.TryParse(args[i + 1], out var c)) capital = c;
        if (args[i] == "--tag" && i + 1 < args.Length) tag = args[i + 1];
    }

    Console.WriteLine("[Backtest] Initializing full 5-tier agent pipeline...");
    var (candles, symbol) = await ResolveCandles(source, interval, totalCandles);

    if (candles.Count < 100)
    {
        Console.WriteLine($"[Backtest] ERROR: Insufficient data ({candles.Count} candles). Need at least 100.");
        return 1;
    }

    Console.WriteLine($"[Backtest] {candles.Count} candles ({candles[0].Timestamp:d} \u2192 {candles[^1].Timestamp:d}) | Capital: {capital:C}");

    var coordinator = BuildFullCoordinator();
    var taSvc = new SimpleTechnicalAnalysis();
    var backtester = new BacktestEngineApp(taSvc, coordinator,
        loggerFactory.CreateLogger<BacktestEngineApp>());

    var result = await backtester.RunAsync(candles, symbol, capital).ConfigureAwait(false);

    PrintBacktestResult(result);

    var store = new BacktestResultStore();
    var path = await store.SaveBacktestAsync(result, tag).ConfigureAwait(false);
    Console.WriteLine($"\n[Backtest] Result saved to: {path}");

    return 0;
}

async Task<int> RunWalkForward(string[] args)
{
    string source = args.Length > 1 ? args[1] : "BTCUSDT";
    string interval = args.Length > 2 ? args[2] : "1h";
    int totalCandles = args.Length > 3 && int.TryParse(args[3], out var tc) ? tc : 3000;
    int trainBars = 500, testBars = 100, stepBars = 100;
    decimal capital = 100_000m;
    string tag = "";

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--train" && i + 1 < args.Length && int.TryParse(args[i + 1], out var t)) trainBars = t;
        if (args[i] == "--test" && i + 1 < args.Length && int.TryParse(args[i + 1], out var te)) testBars = te;
        if (args[i] == "--step" && i + 1 < args.Length && int.TryParse(args[i + 1], out var s)) stepBars = s;
        if (args[i] == "--capital" && i + 1 < args.Length && decimal.TryParse(args[i + 1], out var c)) capital = c;
        if (args[i] == "--tag" && i + 1 < args.Length) tag = args[i + 1];
    }

    Console.WriteLine("[WalkForward] Initializing full 5-tier agent pipeline...");
    var (candles, symbol) = await ResolveCandles(source, interval, totalCandles);

    if (candles.Count < trainBars + testBars)
    {
        Console.WriteLine($"[WalkForward] ERROR: Need at least {trainBars + testBars} candles, got {candles.Count}.");
        return 1;
    }

    int expectedWindows = (candles.Count - trainBars - testBars) / stepBars + 1;
    Console.WriteLine($"[WalkForward] {candles.Count} candles | train={trainBars} test={testBars} step={stepBars} | {expectedWindows} windows");

    var coordinator = BuildFullCoordinator();
    var taSvc = new SimpleTechnicalAnalysis();
    var engine = new WalkForwardEngine(taSvc, coordinator,
        loggerFactory.CreateLogger<WalkForwardEngine>());

    var result = await engine.RunAsync(candles, symbol, trainBars, testBars, stepBars, capital).ConfigureAwait(false);

    PrintWalkForwardResult(result);

    var store = new BacktestResultStore();
    var path = await store.SaveWalkForwardAsync(result, tag).ConfigureAwait(false);
    Console.WriteLine($"\n[WalkForward] Result saved to: {path}");

    return 0;
}

/// <summary>
/// End-to-end validation pipeline: fetch data -> train ML -> backtest -> walk-forward -> verdict.
/// This is the single command that proves whether an edge exists.
/// </summary>
async Task<int> RunValidate(string[] args)
{
    string symbol = args.Length > 1 ? args[1].ToUpperInvariant() : "BTCUSDT";
    string interval = args.Length > 2 ? args[2] : "1h";
    // Default: ~2 years of hourly candles (24h * 365d * 2y = 17520)
    int totalCandles = args.Length > 3 && int.TryParse(args[3], out var tc) ? tc : 17520;
    decimal capital = 100_000m;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--capital" && i + 1 < args.Length && decimal.TryParse(args[i + 1], out var c)) capital = c;
    }

    Console.WriteLine("============================================================");
    Console.WriteLine($"  EDGE VALIDATION PIPELINE: {symbol} {interval}");
    Console.WriteLine("============================================================");
    Console.WriteLine();

    // === Step 1: Fetch data ===
    Console.WriteLine("[Step 1/5] Fetching historical data...");
    using var http = new HttpClient();
    var fetcher = new BinanceHistoricalDataService(http,
        loggerFactory.CreateLogger<BinanceHistoricalDataService>());
    var allCandles = await fetcher.FetchCandlesAsync(symbol, interval, totalCandles).ConfigureAwait(false);

    if (allCandles.Count < 1000)
    {
        Console.WriteLine($"[ABORT] Only {allCandles.Count} candles available. Need 1000+ for meaningful validation.");
        return 1;
    }

    Console.WriteLine($"  Got {allCandles.Count} candles: {allCandles[0].Timestamp:d} \u2192 {allCandles[^1].Timestamp:d}");

    // Save raw data for reproducibility
    string dataFile = $"{symbol}_{interval}_validate.csv";
    await BinanceHistoricalDataService.SaveToCsvAsync(allCandles, dataFile).ConfigureAwait(false);
    Console.WriteLine($"  Saved to: {dataFile}");
    Console.WriteLine();

    // === Step 2: Train ML model on first 60% ===
    Console.WriteLine("[Step 2/5] Training ML model on first 60% of data...");
    int trainEnd = (int)(allCandles.Count * 0.6);
    var trainCandles = allCandles.GetRange(0, trainEnd);
    var testCandles = allCandles.GetRange(trainEnd, allCandles.Count - trainEnd);

    var trainingSet = FeatureSetBuilder.BuildTrainingSet(trainCandles);
    Console.WriteLine($"  Training set: {trainingSet.Count} samples from {trainCandles[0].Timestamp:d} \u2192 {trainCandles[^1].Timestamp:d}");

    string modelPath = $"{symbol}_validate_model.zip";
    double rSquared = 0;
    if (trainingSet.Count >= 100)
    {
        var trainer = new AlphaModelTrainer(loggerFactory.CreateLogger<AlphaModelTrainer>());
        rSquared = trainer.TrainAndSaveModel(trainingSet, modelPath);
        Console.WriteLine($"  Model R\u00b2: {rSquared:F4} ({(rSquared > 0.3 ? "usable" : "weak")})");
    }
    else
    {
        Console.WriteLine($"  WARNING: Insufficient training data ({trainingSet.Count} samples). Skipping ML training.");
    }
    Console.WriteLine();

    // === Step 3: Out-of-sample backtest on remaining 40% ===
    Console.WriteLine("[Step 3/5] Running OUT-OF-SAMPLE backtest on remaining 40%...");
    Console.WriteLine($"  Test period: {testCandles[0].Timestamp:d} \u2192 {testCandles[^1].Timestamp:d} ({testCandles.Count} candles)");

    var coordinator = BuildFullCoordinator();
    var taSvc = new SimpleTechnicalAnalysis();
    var backtester = new BacktestEngineApp(taSvc, coordinator,
        loggerFactory.CreateLogger<BacktestEngineApp>());

    var oosResult = await backtester.RunAsync(testCandles, symbol, capital).ConfigureAwait(false);
    Console.WriteLine();
    PrintBacktestResult(oosResult);
    Console.WriteLine();

    // === Step 4: Walk-forward validation ===
    Console.WriteLine("[Step 4/5] Running walk-forward analysis (rolling retrain)...");
    int trainBars = Math.Max(500, allCandles.Count / 6);  // ~6 months per train window
    int testBars = Math.Max(100, allCandles.Count / 24);   // ~1 month per test window
    int stepBars = testBars;

    var wfEngine = new WalkForwardEngine(taSvc, coordinator,
        loggerFactory.CreateLogger<WalkForwardEngine>());
    var wfResult = await wfEngine.RunAsync(allCandles, symbol, trainBars, testBars, stepBars, capital).ConfigureAwait(false);
    Console.WriteLine();
    PrintWalkForwardResult(wfResult);
    Console.WriteLine();

    // === Step 5: Feature drift check (train vs test distributions) ===
    Console.WriteLine("[Step 5/5] Checking feature distribution drift (train vs test)...");
    var trainFeatures = FeatureSetBuilder.BuildTrainingSet(trainCandles).Select(s => s.Features).ToList();
    var testFeatures = FeatureSetBuilder.BuildTrainingSet(testCandles).Select(s => s.Features).ToList();

    if (trainFeatures.Count >= 10 && testFeatures.Count >= 10)
    {
        var monitor = new FeatureDriftMonitor(loggerFactory.CreateLogger<FeatureDriftMonitor>(), featureDimensions: 5);
        monitor.SetBaselineFromData(trainFeatures);
        foreach (var vec in testFeatures) monitor.Observe(vec);
        var drift = monitor.GetDriftReport();

        Console.WriteLine($"  Drift status: {(drift.OverallHealthy ? "HEALTHY" : "DRIFT DETECTED")} ({drift.DriftedFeatureCount}/{drift.Features.Count} features drifted)");
        foreach (var f in drift.Features)
            Console.WriteLine($"    {(f.IsDrifted ? "!!" : "OK")} {f.FeatureName}: train \u03bc={f.BaselineMean:F4} \u2192 test \u03bc={f.LiveMean:F4}");
    }
    Console.WriteLine();

    // === VERDICT ===
    Console.WriteLine("============================================================");
    Console.WriteLine("  VERDICT");
    Console.WriteLine("============================================================");
    Console.WriteLine();

    bool hasTrades = oosResult.TotalTrades >= 30;
    bool positivePnl = oosResult.TotalPnL > 0;
    bool goodSharpe = oosResult.SharpeRatio >= 1.0;
    bool goodPF = oosResult.ProfitFactor >= 1.5;
    bool acceptableDD = oosResult.MaxDrawdownPercent <= 20;
    bool wfConsistent = wfResult.TotalOosTrades >= 30 && wfResult.SharpeRatio > 0.5;

    Console.WriteLine($"  [{(hasTrades ? "PASS" : "FAIL")}] Sufficient trades: {oosResult.TotalTrades} (need 30+)");
    Console.WriteLine($"  [{(positivePnl ? "PASS" : "FAIL")}] Positive PnL: {oosResult.TotalPnL:C}");
    Console.WriteLine($"  [{(goodSharpe ? "PASS" : "FAIL")}] Sharpe >= 1.0: {oosResult.SharpeRatio:F2}");
    Console.WriteLine($"  [{(goodPF ? "PASS" : "FAIL")}] Profit Factor >= 1.5: {oosResult.ProfitFactor:F2}x");
    Console.WriteLine($"  [{(acceptableDD ? "PASS" : "FAIL")}] Max Drawdown <= 20%: {oosResult.MaxDrawdownPercent:F2}%");
    Console.WriteLine($"  [{(wfConsistent ? "PASS" : "FAIL")}] Walk-forward consistent: Sharpe={wfResult.SharpeRatio:F2}, Trades={wfResult.TotalOosTrades}");
    Console.WriteLine();

    int passCount = new[] { hasTrades, positivePnl, goodSharpe, goodPF, acceptableDD, wfConsistent }.Count(x => x);

    if (passCount == 6)
    {
        Console.WriteLine("  >>> EDGE VALIDATED. System shows statistical evidence of profitability.");
        Console.WriteLine("  >>> Next step: Paper trade for 30 days, compare to backtest predictions.");
    }
    else if (passCount >= 4)
    {
        Console.WriteLine("  >>> MARGINAL. Some evidence of edge but not conclusive.");
        Console.WriteLine("  >>> Investigate failing criteria before risking capital.");
    }
    else
    {
        Console.WriteLine("  >>> NO VALIDATED EDGE. System is not ready for live trading.");
        Console.WriteLine("  >>> Focus on improving strategy signals, reducing costs, or finding better features.");
    }
    Console.WriteLine();

    // Save all results
    var store = new BacktestResultStore();
    await store.SaveBacktestAsync(oosResult, "validate-oos").ConfigureAwait(false);
    await store.SaveWalkForwardAsync(wfResult, "validate-wf").ConfigureAwait(false);

    // Cleanup temp model
    try { File.Delete(modelPath); } catch { }

    return passCount >= 4 ? 0 : 1;
}

int RunListResults(string[] args)
{
    string? symbol = args.Length > 1 ? args[1].ToUpperInvariant() : null;
    var store = new BacktestResultStore();

    var backtests = store.ListBacktests(symbol);
    if (backtests.Count > 0)
    {
        Console.WriteLine("=== Backtest Results ===");
        BacktestResultStore.PrintComparison(backtests);
        Console.WriteLine();
    }

    var walkForwards = store.ListWalkForwards(symbol);
    if (walkForwards.Count > 0)
    {
        Console.WriteLine("=== Walk-Forward Results ===");
        Console.WriteLine($"{"RunId",-10} {"Date",-12} {"Symbol",-10} {"Windows",8} {"Trades",7} {"WR%",6} {"PnL",12} {"Sharpe",7} {"Tag"}");
        Console.WriteLine(new string('-', 85));
        foreach (var r in walkForwards)
        {
            var w = r.Result;
            Console.WriteLine($"{r.RunId,-10} {r.Timestamp:yyyy-MM-dd} {w.Symbol,-10} {w.TotalWindows,8} {w.TotalOosTrades,7} {w.WinRate,5:F1}% {w.TotalPnL,11:C} {w.SharpeRatio,6:F2} {r.Tag}");
        }
    }

    if (backtests.Count == 0 && walkForwards.Count == 0)
        Console.WriteLine("No saved results found. Run 'backtest' or 'walk-forward' first.");

    return 0;
}

// ========== Display helpers ==========

static void PrintBacktestResult(BacktestResult r)
{
    Console.WriteLine($"=== Backtest: {r.Symbol} ===");
    Console.WriteLine($"  Period:            {r.StartDate:d} \u2192 {r.EndDate:d}");
    Console.WriteLine($"  Total Trades:      {r.TotalTrades}");
    Console.WriteLine($"  Win Rate:          {r.WinRate:F1}% ({r.WinningTrades}W / {r.LosingTrades}L)");
    Console.WriteLine($"  Total PnL:         {r.TotalPnL:C}");
    Console.WriteLine($"  Starting Capital:  {r.StartingCapital:C}");
    Console.WriteLine($"  Final Equity:      {r.FinalEquity:C}");
    Console.WriteLine($"  Return:            {(r.StartingCapital > 0 ? (double)(r.FinalEquity - r.StartingCapital) / (double)r.StartingCapital * 100 : 0):F2}%");
    Console.WriteLine($"  Profit Factor:     {r.ProfitFactor:F2}x");
    Console.WriteLine($"  Sharpe Ratio:      {r.SharpeRatio:F2}");
    Console.WriteLine($"  Sortino Ratio:     {r.SortinoRatio:F2}");
    Console.WriteLine($"  Max Drawdown:      {r.MaxDrawdownPercent:F2}%");
    Console.WriteLine($"  Max Consec Losses: {r.MaxConsecutiveLosses}");
    Console.WriteLine($"  Slippage Cost:     {r.TotalSlippageCost:C}");
    Console.WriteLine($"  Avg R/R:           {r.AverageRR:F2}");
}

static void PrintWalkForwardResult(WalkForwardResult result)
{
    Console.WriteLine($"=== Walk-Forward: {result.Symbol} ===");
    Console.WriteLine($"  Windows:           {result.TotalWindows}");
    Console.WriteLine($"  OOS Trades:        {result.TotalOosTrades}");
    Console.WriteLine($"  Win Rate:          {result.WinRate:F1}%");
    Console.WriteLine($"  Total PnL:         {result.TotalPnL:C}");
    Console.WriteLine($"  Profit Factor:     {result.ProfitFactor:F2}x");
    Console.WriteLine($"  Sharpe Ratio:      {result.SharpeRatio:F2}");
    Console.WriteLine($"  Sortino Ratio:     {result.SortinoRatio:F2}");
    Console.WriteLine($"  Max Drawdown:      {result.MaxDrawdownPercent:F2}%");
    Console.WriteLine($"  Max Consec Losses: {result.MaxConsecutiveLosses}");
    Console.WriteLine($"  Avg Model R\u00b2:     {result.AverageModelRSquared:F4}");

    if (result.Windows.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  {"Win",-4} {"Train Period",-25} {"Test Period",-25} {"Trades",7} {"PnL",10} {"R\u00b2",6}");
        Console.WriteLine($"  {new string('-', 80)}");
        foreach (var w in result.Windows)
            Console.WriteLine($"  {w.WindowIndex,-4} {w.TrainStart:d}\u2192{w.TrainEnd:d}  {w.TestStart:d}\u2192{w.TestEnd:d}  {w.BacktestResult.TotalTrades,7} {w.BacktestResult.TotalPnL,9:C} {w.ModelRSquared,5:F3}");
    }
}

static void PrintUsage()
{
    Console.WriteLine("ThatsMyQuant CLI \u2014 Quantitative Trading Tools");
    Console.WriteLine();
    Console.WriteLine("ML Commands:");
    Console.WriteLine("  train [data.csv] [model.zip]              Train a FastTree model from candle CSV");
    Console.WriteLine("  evaluate [model.zip] [data.csv]           Evaluate model accuracy on held-out data");
    Console.WriteLine("  drift-check [baseline.csv] [live.csv]     Check feature distribution drift");
    Console.WriteLine();
    Console.WriteLine("Data Commands:");
    Console.WriteLine("  fetch-data [SYMBOL] [interval] [count] [output.csv]");
    Console.WriteLine("                                            Fetch historical candles from Binance");
    Console.WriteLine();
    Console.WriteLine("Backtesting Commands:");
    Console.WriteLine("  backtest <data.csv|SYMBOL> [interval] [count] [--capital N] [--tag T]");
    Console.WriteLine("                                            Run backtest with full agent pipeline");
    Console.WriteLine("  walk-forward <data.csv|SYMBOL> [interval] [count] [--train N] [--test N] [--step N]");
    Console.WriteLine("                                            Run walk-forward out-of-sample analysis");
    Console.WriteLine("  validate <SYMBOL> [interval] [count]      Full validation pipeline (fetch+train+backtest+WF+verdict)");
    Console.WriteLine("  list-results [SYMBOL]                     Compare saved backtest results");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- fetch-data BTCUSDT 1h 2000");
    Console.WriteLine("  dotnet run -- backtest BTCUSDT 1h 2000 --tag baseline");
    Console.WriteLine("  dotnet run -- validate BTCUSDT 1h 17520   # Full 2-year validation");
    Console.WriteLine("  dotnet run -- walk-forward ETHUSDT 1h 3000 --train 500 --test 100");
    Console.WriteLine("  dotnet run -- list-results");
    Console.WriteLine();
    Console.WriteLine("CSV format: Timestamp,Open,High,Low,Close,Volume");
}

/// <summary>
/// Technical analysis baseline for CLI backtesting.
/// Uses RSI + SMA confluence with ATR-based SL/TP — a real signal, not a stub.
/// </summary>
class SimpleTechnicalAnalysis : ITechnicalAnalysisService
{
    public Task<IndicatorSnapshot> ComputeIndicatorsAsync(List<Candle> candles)
        => Task.FromResult(new IndicatorSnapshot());

    public Task<TradeDecision> AnalyzeAsync(Signal signal, List<Candle> candles, RiskParameters risk)
    {
        if (candles.Count < 30)
            return Task.FromResult(new TradeDecision { ShouldTrade = false });

        decimal price = candles[^1].Close;

        // 20-bar SMA trend filter
        decimal sma20 = candles.Skip(candles.Count - 20).Average(c => c.Close);

        // Simple RSI(14) — approximate with Wilder's
        double avgGain = 0, avgLoss = 0;
        for (int i = candles.Count - 14; i < candles.Count; i++)
        {
            decimal change = candles[i].Close - candles[i - 1].Close;
            if (change > 0) avgGain += (double)change;
            else avgLoss += (double)(-change);
        }
        avgGain /= 14; avgLoss /= 14;
        double rsi = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);

        // ATR(14) for stops
        decimal atr = candles.Skip(candles.Count - 14).Average(c => c.High - c.Low);
        if (atr <= 0) return Task.FromResult(new TradeDecision { ShouldTrade = false });

        // Entry conditions: trend + RSI confluence
        bool bullish = price > sma20 && rsi > 40 && rsi < 70;  // uptrend + not overbought
        bool bearish = price < sma20 && rsi < 60 && rsi > 30;  // downtrend + not oversold

        if (!bullish && !bearish)
            return Task.FromResult(new TradeDecision { ShouldTrade = false });

        var side = bullish ? TradeSide.Buy : TradeSide.Sell;
        decimal sl = bullish ? price - atr * 2m : price + atr * 2m;
        decimal tp = bullish ? price + atr * 3m : price - atr * 3m;

        // Confidence scales with RSI extremity: deeper RSI = more confident
        int confidence = bullish
            ? Math.Min(80, 55 + (int)((70 - rsi) / 30 * 25))
            : Math.Min(80, 55 + (int)((rsi - 30) / 30 * 25));

        return Task.FromResult(new TradeDecision
        {
            ShouldTrade = true,
            Side = side,
            Confidence = confidence,
            StopLoss = sl,
            TakeProfit = tp,
            PositionSizePercent = 0.02m, // 2% of equity per trade
            Reasoning = $"RSI({rsi:F0})+SMA20 {(bullish ? "bullish" : "bearish")} confluence"
        });
    }
}
