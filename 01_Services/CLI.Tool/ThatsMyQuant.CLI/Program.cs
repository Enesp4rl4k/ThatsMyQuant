using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.Strategy;
using Microsoft.EntityFrameworkCore;
using ThatsMyQuant.Infrastructure;
using ThatsMyQuant.Infrastructure.Services.Validators;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Options;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddThatsMyQuantInfrastructure(builder.Configuration);
builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();
builder.Logging.AddConsole();

var host = builder.Build();

// Ensure SQLite compatibility
using (var scope = host.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ThatsMyQuant.Infrastructure.Persistence.QuantDbContext>>();
    using var db = dbFactory.CreateDbContext();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await ThatsMyQuant.Infrastructure.Persistence.SqliteCompatibilityBootstrapper.EnsureCompatibleSchemaAsync(db, builder.Configuration, logger);
}

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "backtest":
        await RunBacktestAsync(host.Services, args);
        break;

    case "walkforward":
        await RunWalkForwardAsync(host.Services, args);
        break;

    case "hftbacktest":
        await RunHftBacktestAsync(host.Services, args);
        break;

    case "paper":
        await RunPaperAsync(host.Services, args);
        break;

    case "signalaudit":
        await RunSignalAuditAsync(host.Services, args);
        break;

    case "help":
    default:
        PrintUsage();
        break;
}

static async Task RunBacktestAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var orchestrator = scope.ServiceProvider.GetRequiredService<BacktestOrchestrator>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<BacktestOrchestrator>>();
    var qualityGate = scope.ServiceProvider.GetService<IOptions<BacktestQualityGateOptions>>()?.Value ?? new BacktestQualityGateOptions();
    var profitOptions = scope.ServiceProvider.GetService<IOptions<ProfitMaximizationOptions>>()?.Value ?? new ProfitMaximizationOptions();

    var symbolsArg = GetArg(args, "--symbols", "");
    var symbols = !string.IsNullOrWhiteSpace(symbolsArg)
        ? symbolsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        : new[] { GetArg(args, "--symbol", "BTC-USDT") };
    var timeframe = GetArg(args, "--timeframe", "1h");
    var riskProfile = GetArg(args, "--risk-profile", "Moderate");
    var capital = decimal.Parse(GetArg(args, "--capital", "100000"));
    var daysBack = int.Parse(GetArg(args, "--days", "365"));
    var benchmarkAuto = bool.Parse(GetArg(args, "--benchmark-auto", "false"));
    var fastScreen = bool.Parse(GetArg(args, "--fast-screen", "false"));
    var autoDeepValidate = bool.Parse(GetArg(args, "--auto-deep-validate", "true"));
    var readinessMatrix = bool.Parse(GetArg(args, "--readiness-matrix", "false"));
    var readinessRuns = Math.Clamp(int.Parse(GetArg(args, "--readiness-runs", "3")), 1, 10);
    var stabilizeProfiles = bool.Parse(GetArg(args, "--stabilize-profiles", "false"));
    var readinessReportPath = GetArg(args, "--readiness-report", "logs/live_readiness_report.json");
    var readinessFastScreen = bool.Parse(GetArg(args, "--readiness-fast-screen", "true"));
    var readinessProfileScope = GetArg(args, "--readiness-profile", "");
    var readinessHorizonScope = GetArg(args, "--readiness-days-scope", "");

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);
    var perSymbolCapital = capital / symbols.Length;

    Console.WriteLine("=== ThatsMyQuant Backtest CLI ===");
    Console.WriteLine($"Symbols:        {string.Join(", ", symbols)}");
    Console.WriteLine($"Timeframe:      {timeframe}");
    Console.WriteLine($"Risk Profile:   {riskProfile}");
    Console.WriteLine($"Execution Mode: {(fastScreen ? "FAST_SCREEN" : "FULL")}");
    Console.WriteLine($"Period:         {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Total Capital:  {capital:N0}  (split {perSymbolCapital:N0} per symbol)");
    Console.WriteLine($"Quality Gate:   90d(PnL>{qualityGate.ShortMinPnl:N0},Sharpe>={qualityGate.ShortMinSharpe:F2},R:R>={qualityGate.ShortMinAverageRr:F2}) | 365d(PnL>{qualityGate.LongMinPnl:N0},Sharpe>={qualityGate.LongMinSharpe:F2},R:R>={qualityGate.LongMinAverageRr:F2})");
    Console.WriteLine($"Reliability:    PBO<={qualityGate.MaxPbo:F2}, MinTrades>={qualityGate.MinTrades}, MaxDD<={qualityGate.MaxDrawdownPercent:F1}%");
    if (readinessMatrix || stabilizeProfiles)
    {
        Console.WriteLine($"Readiness Mode: ENABLED (runs={readinessRuns}, strict=ON)");
    }
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var sessions = new List<BacktestSessionResult>();
    foreach (var sym in symbols)
    {
        Console.WriteLine($"--- {sym} ---");
        var s = await orchestrator.RunAutomatedSessionAsync(sym, timeframe, start, end, perSymbolCapital, riskProfile, fastScreen);
        sessions.Add(s);
        Console.WriteLine($"  {sym}: Trades={s.TotalTrades} PnL={s.TotalPnL:N2} Sharpe={s.SharpeRatio:F2} WR={s.WinRate:F1}% R:R={s.AverageRR:F2} Score={s.ObjectiveScore:F4} HardGate={(s.HardGatePassed ? "YES" : "NO")} Parity={(s.ParityPassed ? "OK" : "GAP")} WFE={s.WalkForwardEfficiency:F2} WF={(s.WalkForwardPassed ? "PASS" : "FAIL")} Gate={(s.WalkForwardGateApplied ? "ON" : "OFF")} Pass={(s.BacktestPass ? "YES" : "NO")}");
        Console.WriteLine($"    Coverage: {s.CoverageSummary}");
        if (s.WfoProfileApplied)
        {
            Console.WriteLine($"    WFO primary params: {s.WfoPrimaryParamsSummary} (aggregate robust={s.WfoParamSelectionRobust})");
        }
        if (s.EntryRejectReasons.Count > 0)
        {
            var topRejects = s.EntryRejectReasons
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => $"{kv.Key}:{kv.Value}");
            Console.WriteLine($"    Entry rejects(top): {string.Join(", ", topRejects)}");
        }
        if (!s.ParityPassed)
        {
            Console.WriteLine($"    Missing coverage: {s.MissingCoverageFlags}");
        }

        if (fastScreen && autoDeepValidate)
        {
            var profile = profitOptions.Resolve(riskProfile);
            var quickPass = s.TotalPnL > 0m && s.SharpeRatio >= profile.MinSharpe && s.AverageRR >= profile.MinAverageRr;
            Console.WriteLine($"    Quick screen verdict: {(quickPass ? "PASS" : "FAIL")} (PnL>0, Sharpe>={profile.MinSharpe:F2}, R:R>={profile.MinAverageRr:F2})");
            if (quickPass)
            {
                Console.WriteLine("    Triggering deep validation (PBO + WF)...");
                var deep = await orchestrator.RunAutomatedSessionAsync(sym, timeframe, start, end, perSymbolCapital, riskProfile, false);
                Console.WriteLine($"    Deep validation: Trades={deep.TotalTrades} PnL={deep.TotalPnL:N2} Sharpe={deep.SharpeRatio:F2} R:R={deep.AverageRR:F2} Score={deep.ObjectiveScore:F4} HardGate={(deep.HardGatePassed ? "YES" : "NO")} Parity={(deep.ParityPassed ? "OK" : "GAP")} PBO={deep.PboScore:F2} WF={(deep.WalkForwardPassed ? "PASS" : "FAIL")} Pass={(deep.BacktestPass ? "YES" : "NO")}");
                if (deep.WfoProfileApplied)
                {
                    Console.WriteLine($"    Deep WFO primary params: {deep.WfoPrimaryParamsSummary} (robust={deep.WfoParamSelectionRobust})");
                }
            }
        }
    }
    sw.Stop();

    var totalTrades = sessions.Sum(s => s.TotalTrades);
    var totalPnl = sessions.Sum(s => s.TotalPnL);
    var wins = sessions.Sum(s => s.WinningTrades);
    var losses = sessions.Sum(s => s.LosingTrades);
    var avgWin = wins > 0 ? sessions.Where(s => s.WinningTrades > 0).Sum(s => s.AverageWin * s.WinningTrades) / wins : 0m;
    var avgLoss = losses > 0 ? sessions.Where(s => s.LosingTrades > 0).Sum(s => s.AverageLoss * s.LosingTrades) / losses : 0m;
    var avgRR = avgLoss != 0m ? Math.Abs((double)(avgWin / avgLoss)) : 0.0;
    var winRate = totalTrades > 0 ? 100.0 * wins / totalTrades : 0.0;
    var aggSharpe = sessions.Count > 0 ? sessions.Average(s => s.SharpeRatio) : 0.0;
    var aggSortino = sessions.Count > 0 ? sessions.Average(s => s.SortinoRatio) : 0.0;
    var aggMaxDd = sessions.Count > 0 ? sessions.Max(s => s.MaxDrawdownPercent) : 0.0;
    var aggPbo = sessions.Count > 0 ? sessions.Average(s => s.PboScore) : 0.0;
    var allPass = sessions.All(s => s.BacktestPass);

    Console.WriteLine();
    Console.WriteLine("=== Backtest Results (Portfolio) ===");
    Console.WriteLine($"Symbols:          {sessions.Count}");
    Console.WriteLine($"Total Trades:     {totalTrades}");
    Console.WriteLine($"Total PnL:        {totalPnl:N2}  ({(totalPnl / capital):P2})");
    Console.WriteLine($"Sharpe (avg):     {aggSharpe:F3}");
    Console.WriteLine($"Sortino (avg):    {aggSortino:F3}");
    Console.WriteLine($"Worst Max DD:     {aggMaxDd:F2}%");
    Console.WriteLine($"Win Rate:         {winRate:F1}%  ({wins}W / {losses}L)");
    Console.WriteLine($"Avg Win:          {avgWin:N2}");
    Console.WriteLine($"Avg Loss:         {avgLoss:N2}");
    Console.WriteLine($"Avg R:R:          {avgRR:F2}");
    Console.WriteLine($"PBO (avg):        {aggPbo:F3}");
    Console.WriteLine($"All Pass:         {(allPass ? "YES" : "NO")}");
    Console.WriteLine($"\nCompleted in {sw.Elapsed.TotalSeconds:F1}s");

    if (benchmarkAuto && symbols.Length == 1)
    {
        Console.WriteLine("\n=== Auto Benchmark Leaderboard (90d vs 365d) ===");
        var benchmarkRows = new List<(int Days, BacktestSessionResult Session)>();
        foreach (var horizon in new[] { 90, 365 })
        {
            var bEnd = DateTime.UtcNow;
            var bStart = bEnd.AddDays(-horizon);
            var benchmarkSession = await orchestrator.RunAutomatedSessionAsync(symbols[0], timeframe, bStart, bEnd, capital, riskProfile);
            benchmarkRows.Add((horizon, benchmarkSession));
        }

        Console.WriteLine("Days | Trades | PnL | Sharpe | R:R | Score | HardGate | Parity | PBO | WF | Pass");
        foreach (var row in benchmarkRows)
        {
            Console.WriteLine(
                $"{row.Days,4} | {row.Session.TotalTrades,6} | {row.Session.TotalPnL,8:N2} | {row.Session.SharpeRatio,6:F2} | {row.Session.AverageRR,4:F2} | {row.Session.ObjectiveScore,5:F3} | {(row.Session.HardGatePassed ? "YES" : "NO"),8} | {(row.Session.ParityPassed ? "OK" : "GAP"),6} | {row.Session.PboScore,4:F2} | {(row.Session.WalkForwardPassed ? "PASS" : "FAIL"),4} | {(row.Session.BacktestPass ? "YES" : "NO"),4}");
        }

        var winner = benchmarkRows
            .OrderByDescending(r => r.Session.HardGatePassed)
            .ThenByDescending(r => r.Session.ObjectiveScore)
            .ThenBy(r => r.Session.MaxDrawdownPercent)
            .FirstOrDefault();
        Console.WriteLine($"Winner Horizon: {winner.Days}d (Score={winner.Session.ObjectiveScore:F3}, HardGate={(winner.Session.HardGatePassed ? "YES" : "NO")})");
    }

    if ((readinessMatrix || stabilizeProfiles) && symbols.Length == 1)
    {
        var report = await RunStrictReadinessMatrixAsync(
            orchestrator,
            symbols[0],
            timeframe,
            capital,
            readinessRuns,
            qualityGate,
            readinessReportPath,
            readinessFastScreen,
            readinessProfileScope,
            readinessHorizonScope);
        PrintStrictReadinessReport(report);
    }
}

static async Task RunHftBacktestAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var orchestrator = scope.ServiceProvider.GetRequiredService<BacktestOrchestrator>();

    var symbol = GetArg(args, "--symbol", "BTC-USDT");
    var timeframe = GetArg(args, "--timeframe", "5m");
    var capital = decimal.Parse(GetArg(args, "--capital", "100000"), System.Globalization.CultureInfo.InvariantCulture);
    var daysBack = int.Parse(GetArg(args, "--days", "7"));
    var ticksCsvPath = GetArg(args, "--ticks-csv", "");
    var orderBooksCsvPath = GetArg(args, "--orderbooks-csv", "");

    if (string.IsNullOrWhiteSpace(ticksCsvPath) || string.IsNullOrWhiteSpace(orderBooksCsvPath))
    {
        Console.WriteLine("HFT backtest now requires L2 replay inputs.");
        Console.WriteLine("Required: --ticks-csv <path> --orderbooks-csv <path>");
        return;
    }

    if (!File.Exists(ticksCsvPath))
    {
        Console.WriteLine($"Ticks CSV not found: {ticksCsvPath}");
        return;
    }

    if (!File.Exists(orderBooksCsvPath))
    {
        Console.WriteLine($"OrderBooks CSV not found: {orderBooksCsvPath}");
        return;
    }

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine("=== ThatsMyQuant HFT Backtest CLI ===");
    Console.WriteLine($"Symbol:     {symbol}");
    Console.WriteLine($"Timeframe:  {timeframe}");
    Console.WriteLine($"Period:     {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Capital:    {capital:N0}");
    Console.WriteLine($"Ticks CSV:  {ticksCsvPath}");
    Console.WriteLine($"Books CSV:  {orderBooksCsvPath}");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var s = await orchestrator.RunHftTickReplaySessionAsync(
        symbol,
        timeframe,
        start,
        end,
        capital,
        ticksCsvPath,
        orderBooksCsvPath);
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== HFT Backtest Results ===");
    Console.WriteLine($"Trades:      {s.TotalTrades}");
    Console.WriteLine($"PnL:         {s.TotalPnL:N2}  ({(s.TotalPnL / capital):P2})");
    Console.WriteLine($"Sharpe:      {s.SharpeRatio:F3}");
    Console.WriteLine($"Sortino:     {s.SortinoRatio:F3}");
    Console.WriteLine($"Max DD:      {s.MaxDrawdownPercent:F2}%");
    Console.WriteLine($"Win Rate:    {s.WinRate:F1}%  ({s.WinningTrades}W / {s.LosingTrades}L)");
    Console.WriteLine($"Avg Win:     {s.AverageWin:N2}");
    Console.WriteLine($"Avg Loss:    {s.AverageLoss:N2}");
    Console.WriteLine($"Avg R:R:     {s.AverageRR:F2}");
    Console.WriteLine($"Pass:        {(s.BacktestPass ? "YES" : "NO")}");
    Console.WriteLine($"\nCompleted in {sw.Elapsed.TotalSeconds:F1}s");
}

static async Task RunWalkForwardAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var engine = scope.ServiceProvider.GetRequiredService<WalkForwardEngine>();
    var histService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.IHistoricalMarketDataService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<WalkForwardEngine>>();

    var symbol = GetArg(args, "--symbol", "BTC-USDT");
    var timeframe = GetArg(args, "--timeframe", "1h");
    var capital = decimal.Parse(GetArg(args, "--capital", "100000"));
    var daysBack = int.Parse(GetArg(args, "--days", "180"));
    var windows = int.Parse(GetArg(args, "--windows", "6"));
    var trainRatio = double.Parse(GetArg(args, "--train-ratio", "0.7"));

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine($"=== ThatsMyQuant Walk-Forward CLI ===");
    Console.WriteLine($"Symbol:      {symbol}");
    Console.WriteLine($"Timeframe:   {timeframe}");
    Console.WriteLine($"Period:      {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Windows:     {windows}");
    Console.WriteLine($"Train Ratio: {trainRatio:P0}");
    Console.WriteLine();

    var candles = await histService.GetHistoricalCandlesAsync(symbol, timeframe, start, end);

    if (candles.Count < 200)
    {
        Console.WriteLine($"Insufficient data: {candles.Count} candles. Need at least 200.");
        return;
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await engine.RunAsync(candles, symbol, timeframe, windows, trainRatio, capital);
    sw.Stop();

    Console.WriteLine($"\n=== Walk-Forward Results ===");
    Console.WriteLine($"Windows:          {result.WindowResults.Count}");
    Console.WriteLine($"OOS Sharpe:       {result.AggregateOosSharpe:F3}");
    Console.WriteLine($"OOS PnL:          {result.AggregateOosPnL:N2}");
    Console.WriteLine($"OOS Win Rate:     {result.AggregateOosWinRate:P1}");
    Console.WriteLine($"Walk-Forward Eff: {result.WalkForwardEfficiency:P1}");
    Console.WriteLine($"Regime Stability: {result.RegimeStabilityScore:F3}");
    Console.WriteLine($"Overall Pass:     {(result.IsRobust ? "YES" : "NO")}");
    Console.WriteLine($"\nCompleted in {sw.Elapsed.TotalSeconds:F1}s");

    foreach (var w in result.WindowResults)
    {
        Console.WriteLine($"  Window {w.WindowIndex}: Train Sharpe={w.TrainSharpe:F2}, OOS Sharpe={w.OosSharpe:F2}, OOS PnL={w.OosPnL:N2}, Trades={w.OosTrades}");
    }
}

static async Task RunSignalAuditAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var audit = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Analytics.SignalQualityAuditService>();
    var histService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.IHistoricalMarketDataService>();

    var symbol = GetArg(args, "--symbol", "BTC-USDT");
    var timeframe = GetArg(args, "--timeframe", "4h");
    var daysBack = int.Parse(GetArg(args, "--days", "365"));
    var csvOut = GetArg(args, "--csv", "");
    var jsonOut = GetArg(args, "--json", "");

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine("=== ThatsMyQuant Signal Quality Audit ===");
    Console.WriteLine($"Symbol:    {symbol}");
    Console.WriteLine($"Timeframe: {timeframe}");
    Console.WriteLine($"Period:    {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine();
    Console.WriteLine("Measuring: Information Coefficient (confidence vs forward return),");
    Console.WriteLine("           signal-alone cumulative PnL (no risk mgmt), and conditional");
    Console.WriteLine("           performance by strategy / regime / confidence bucket.");
    Console.WriteLine();

    var candles = await histService.GetHistoricalCandlesAsync(symbol, timeframe, start, end);
    if (candles.Count < 200)
    {
        Console.WriteLine($"Insufficient data: {candles.Count} candles.");
        return;
    }
    Console.WriteLine($"Loaded {candles.Count} candles. Walking...");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var records = await audit.CollectAsync(symbol, candles);
    sw.Stop();

    var report = audit.BuildReport(symbol, timeframe, start, end, candles.Count, records);

    Console.WriteLine();
    Console.WriteLine($"=== Report (walked {report.CandlesEvaluated} candles in {sw.Elapsed.TotalSeconds:F1}s) ===");
    Console.WriteLine($"Candidate signals (ShouldTrade=true):  {report.Candidates}");
    Console.WriteLine();
    Console.WriteLine("-- Overall (all candidates) --");
    PrintStats(report.Overall);
    Console.WriteLine();
    Console.WriteLine("-- Information Coefficient (Spearman rank: confidence vs forward return) --");
    Console.WriteLine($"  IC@1   {report.IC.Fwd1,7:F4}     IC@5   {report.IC.Fwd5,7:F4}     IC@10  {report.IC.Fwd10,7:F4}");
    Console.WriteLine($"  IC@20  {report.IC.Fwd20,7:F4}     IC@50  {report.IC.Fwd50,7:F4}");
    Console.WriteLine("  (|IC| >= 0.05 is meaningful for rule-based signals; >= 0.10 is strong.)");
    Console.WriteLine();
    Console.WriteLine("-- Signal-alone cumulative % return (no risk mgmt, no costs) --");
    Console.WriteLine($"  Fwd10 horizon sum:  {report.SignalAloneCumulativeReturnPercent,10:F2} %");
    Console.WriteLine($"  Fwd20 horizon sum:  {report.SignalAloneCumulativeReturn20Percent,10:F2} %");
    Console.WriteLine();
    Console.WriteLine("-- By Strategy Tag --");
    foreach (var (tag, s) in report.ByStrategy.OrderByDescending(kv => kv.Value.N))
    {
        Console.WriteLine($"  [{tag}]  N={s.N}  fwd10 mean={s.MeanFwd10,7:F3} %  median={s.MedianFwd10,7:F3} %  hit={s.HitRateFwd10:P1}");
    }
    Console.WriteLine();
    Console.WriteLine("-- By Regime --");
    foreach (var (tag, s) in report.ByRegime.OrderByDescending(kv => kv.Value.N))
    {
        Console.WriteLine($"  [{tag}]  N={s.N}  fwd10 mean={s.MeanFwd10,7:F3} %  median={s.MedianFwd10,7:F3} %  hit={s.HitRateFwd10:P1}");
    }
    Console.WriteLine();
    Console.WriteLine("-- By Confidence Bucket --");
    foreach (var bucket in new[] { "<60", "60-69", "70-79", "80-89", "90-100" })
    {
        if (!report.ByConfidenceBucket.TryGetValue(bucket, out var s)) continue;
        Console.WriteLine($"  [{bucket}]  N={s.N,5}  fwd10 mean={s.MeanFwd10,7:F3} %  fwd20 mean={s.MeanFwd20,7:F3} %  hit10={s.HitRateFwd10:P1}");
    }
    Console.WriteLine();
    Console.WriteLine("-- Per-Feature IC Breakdown (Spearman: feature score vs forward return) --");
    Console.WriteLine($"  {"Feature",-14} {"N",5}  {"IC@10",7}  {"IC@20",7}  {"MeanFwd10(+)",14}  {"MeanFwd10(0/-)",14}  {"Gap",8}  Verdict");
    foreach (var f in new[] { "RSI", "MACD", "EMA", "Bollinger", "Volume", "AlphaTrend", "PMax", "SR", "Pattern", "Regime" })
    {
        if (!report.ByFeatureIc.TryGetValue(f, out var fic)) continue;
        var gap = fic.MeanFwd10WhenPositive - fic.MeanFwd10WhenNonPositive;
        var verdict = fic.IcFwd10 switch
        {
            < -0.10 => "⚠️ ANTI-PREDICTIVE",
            < -0.05 => "⚠️ Mildly inverted",
            > 0.10  => "✅ Strong alpha",
            > 0.05  => "✅ Modest alpha",
            _       => "➖ Noise",
        };
        Console.WriteLine($"  {f,-14} {fic.N,5}  {fic.IcFwd10,7:F4}  {fic.IcFwd20,7:F4}  {fic.MeanFwd10WhenPositive,14:F4}  {fic.MeanFwd10WhenNonPositive,14:F4}  {gap,8:F4}  {verdict}");
    }
    Console.WriteLine();
    Console.WriteLine("-- Verdict --");
    // Sign matters: a strongly NEGATIVE IC means confidence is anti-predictive —
    // higher confidence correlates with WORSE forward returns. That is a different
    // (worse) failure mode than "no signal" and needs to be surfaced distinctly.
    var ic10 = report.IC.Fwd10;
    var ic20 = report.IC.Fwd20;
    var absIc = Math.Max(Math.Abs(ic10), Math.Abs(ic20));
    var signedIc = Math.Abs(ic10) >= Math.Abs(ic20) ? ic10 : ic20;
    var alphaPositive = report.Overall.MeanFwd10 > 0m && report.Overall.MeanFwd20 > 0m;

    if (absIc >= 0.10 && signedIc < 0)
        Console.WriteLine("  INVERTED ALPHA — confidence is ANTI-predictive (|IC|>=0.10 with wrong sign). Fade the score, raise-not-lower low-confidence entries, or rebuild the confidence formula.");
    else if (absIc >= 0.10 && signedIc > 0 && alphaPositive)
        Console.WriteLine("  STRONG ALPHA — signal meaningfully predicts forward returns.");
    else if (absIc >= 0.05 && signedIc > 0 && alphaPositive)
        Console.WriteLine("  MODEST ALPHA — signal has some predictive content; execution/sizing may amplify.");
    else if (alphaPositive)
        Console.WriteLine("  WEAK ALPHA — returns positive but IC near zero; confidence ordering is noise.");
    else
        Console.WriteLine("  NO ALPHA — signal does not predict forward returns. Execution cannot rescue this.");

    if (!string.IsNullOrWhiteSpace(csvOut))
    {
        await WriteCsvAsync(csvOut, records);
        Console.WriteLine($"\nCSV written: {csvOut}");
    }
    if (!string.IsNullOrWhiteSpace(jsonOut))
    {
        var jdir = Path.GetDirectoryName(jsonOut);
        if (!string.IsNullOrWhiteSpace(jdir) && !Directory.Exists(jdir)) Directory.CreateDirectory(jdir);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(jsonOut, json);
        Console.WriteLine($"JSON written: {jsonOut}");
    }
}

static void PrintStats(ThatsMyQuant.Contracts.Models.SignalReturnStats s)
{
    Console.WriteLine($"  N={s.N}");
    Console.WriteLine($"  mean fwd  1: {s.MeanFwd1,7:F3} %   fwd  5: {s.MeanFwd5,7:F3} %   fwd 10: {s.MeanFwd10,7:F3} %");
    Console.WriteLine($"  mean fwd 20: {s.MeanFwd20,7:F3} %   fwd 50: {s.MeanFwd50,7:F3} %   median10: {s.MedianFwd10,7:F3} %");
    Console.WriteLine($"  hit rate  fwd10: {s.HitRateFwd10:P1}     fwd20: {s.HitRateFwd20:P1}");
}

static async Task WriteCsvAsync(string path, IReadOnlyList<ThatsMyQuant.Contracts.Models.SignalAuditRecord> records)
{
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    await using var w = new StreamWriter(path);
    var featureNames = new[] { "RSI", "MACD", "EMA", "Bollinger", "Volume", "AlphaTrend", "PMax", "SR", "Pattern", "Regime" };
    await w.WriteLineAsync("Timestamp,Symbol,ShouldTrade,Side,Confidence,StrategyTag,RegimeTag,EntryPrice,Fwd1,Fwd5,Fwd10,Fwd20,Fwd50," + string.Join(',', featureNames.Select(f => $"F_{f}")));
    foreach (var r in records)
    {
        var featureCols = string.Join(',', featureNames.Select(f => r.FeatureScores.TryGetValue(f, out var v) ? v.ToString() : "0"));
        await w.WriteLineAsync(string.Join(',',
            r.Timestamp.ToString("o"), r.Symbol, r.ShouldTrade, r.Side,
            r.Confidence, r.StrategyTag, r.RegimeTag,
            r.EntryPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Fwd1.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Fwd5.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Fwd10.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Fwd20.ToString(System.Globalization.CultureInfo.InvariantCulture),
            r.Fwd50.ToString(System.Globalization.CultureInfo.InvariantCulture),
            featureCols));
    }
}

static async Task RunPaperAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var paperService = scope.ServiceProvider.GetRequiredService<PaperTradingService>();

    var symbolsCsv = GetArg(args, "--symbols", "BTC-USDT,ETH-USDT");
    var iterations = int.Parse(GetArg(args, "--iterations", "10"));
    var intervalSeconds = int.Parse(GetArg(args, "--interval", "30"));

    var symbols = symbolsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    Console.WriteLine("=== ThatsMyQuant Paper-Trade CLI ===");
    Console.WriteLine($"Symbols:    {string.Join(", ", symbols)}");
    Console.WriteLine($"Iterations: {iterations}");
    Console.WriteLine($"Interval:   {intervalSeconds}s");
    Console.WriteLine();

    for (int i = 1; i <= iterations; i++)
    {
        Console.WriteLine($"--- Iteration {i}/{iterations} ---");
        foreach (var symbol in symbols)
        {
            try
            {
                var result = await paperService.ProcessSymbolAsync(symbol);
                Console.WriteLine($"  [{symbol}] {result}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{symbol}] ERROR: {ex.Message}");
            }
        }

        var p = paperService.Portfolio;
        Console.WriteLine(
            $"  Portfolio: Cash=₺{p.Cash:N2} Equity=₺{p.TotalEquity:N2} Open={p.OpenPositions.Count} Closed={p.ClosedPositions.Count} DailyPnL=₺{p.DailyPnL:N2}");

        if (i < iterations)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== Paper Session Summary ===");
    var portfolio = paperService.Portfolio;
    Console.WriteLine($"Starting balance: ₺{portfolio.InitialBalance:N2}");
    Console.WriteLine($"Final equity:     ₺{portfolio.TotalEquity:N2}");
    Console.WriteLine($"Trades closed:    {portfolio.ClosedPositions.Count}");
    if (portfolio.ClosedPositions.Count > 0)
    {
        var wins = portfolio.ClosedPositions.Count(p => p.RealizedPnL > 0);
        var winRate = (double)wins / portfolio.ClosedPositions.Count;
        Console.WriteLine($"Win rate:         {winRate:P1} ({wins}/{portfolio.ClosedPositions.Count})");
        Console.WriteLine($"Realized PnL:     ₺{portfolio.ClosedPositions.Sum(p => p.RealizedPnL):N2}");
    }
}

static async Task<StrictReadinessReport> RunStrictReadinessMatrixAsync(
    BacktestOrchestrator orchestrator,
    string symbol,
    string timeframe,
    decimal capital,
    int runs,
    BacktestQualityGateOptions qualityGate,
    string reportPath,
    bool fastScreen,
    string profileScope,
    string horizonScope)
{
    var profiles = string.IsNullOrWhiteSpace(profileScope)
        ? new[] { "Low", "Moderate", "Aggressive" }
        : profileScope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var horizons = string.IsNullOrWhiteSpace(horizonScope)
        ? new[] { qualityGate.ShortHorizonDays, qualityGate.LongHorizonDays }
        : horizonScope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var d) ? d : qualityGate.LongHorizonDays)
            .Distinct()
            .ToArray();
    var cells = new List<StrictReadinessCell>();

    foreach (var profile in profiles)
    {
        foreach (var horizon in horizons)
        {
            var runResults = new List<BacktestSessionResult>();
            for (var run = 1; run <= runs; run++)
            {
                var end = DateTime.UtcNow;
                var start = end.AddDays(-horizon);
                var session = await orchestrator.RunAutomatedSessionAsync(symbol, timeframe, start, end, capital, profile, fastScreen);
                runResults.Add(session);
            }

            var allPass = runResults.All(r =>
                r.TotalPnL > 0m &&
                r.HardGatePassed &&
                r.BacktestPass &&
                r.ParityPassed &&
                r.MlReadyPassed);

            var blockers = runResults
                .SelectMany(r => r.ReadinessBlockers ?? Enumerable.Empty<string>())
                .GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}({g.Count()})")
                .ToList();

            cells.Add(new StrictReadinessCell(
                profile,
                horizon,
                runs,
                allPass,
                runResults.Average(r => r.TotalPnL),
                runResults.Average(r => r.SharpeRatio),
                runResults.Average(r => r.AverageRR),
                runResults.All(r => r.HardGatePassed),
                runResults.All(r => r.ParityPassed),
                runResults.All(r => r.MlReadyPassed),
                blockers));
        }
    }

    var report = new StrictReadinessReport(
        DateTime.UtcNow,
        symbol,
        timeframe,
        runs,
        fastScreen,
        cells,
        cells.All(c => c.StrictPass));

    if (File.Exists(reportPath))
    {
        try
        {
            var existingJson = await File.ReadAllTextAsync(reportPath);
            var existing = JsonSerializer.Deserialize<StrictReadinessReport>(existingJson);
            if (existing is not null)
            {
                var merged = existing.Cells
                    .Concat(report.Cells)
                    .GroupBy(c => $"{c.Profile}|{c.Days}")
                    .Select(g => g.OrderByDescending(x => x.Runs).ThenByDescending(x => x.StrictPass).First())
                    .OrderBy(c => c.Profile)
                    .ThenBy(c => c.Days)
                    .ToList();
                report = report with { Cells = merged, LiveReady = merged.All(c => c.StrictPass) };
            }
        }
        catch
        {
            // Keep fresh report when merge cannot be parsed.
        }
    }

    var dir = Path.GetDirectoryName(reportPath);
    if (!string.IsNullOrWhiteSpace(dir))
    {
        Directory.CreateDirectory(dir);
    }
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(reportPath, json);
    return report;
}

static void PrintStrictReadinessReport(StrictReadinessReport report)
{
    Console.WriteLine("\n=== Strict Live Readiness Matrix ===");
    Console.WriteLine("Profile | Days | Runs | StrictPass | AvgPnL | AvgSharpe | AvgR:R | HardGateAll | ParityAll | MlReadyAll | TopBlockers");
    foreach (var cell in report.Cells.OrderBy(c => c.Profile).ThenBy(c => c.Days))
    {
        var topBlockers = cell.TopBlockers.Count > 0 ? string.Join(", ", cell.TopBlockers.Take(3)) : "-";
        Console.WriteLine(
            $"{cell.Profile,8} | {cell.Days,4} | {cell.Runs,4} | {(cell.StrictPass ? "YES" : "NO"),10} | {cell.AvgPnl,7:N2} | {cell.AvgSharpe,9:F2} | {cell.AvgRr,6:F2} | {(cell.HardGateAll ? "YES" : "NO"),11} | {(cell.ParityAll ? "YES" : "NO"),9} | {(cell.MlReadyAll ? "YES" : "NO"),10} | {topBlockers}");
    }
    Console.WriteLine($"\nLive Ready Decision: {(report.LiveReady ? "READY" : "BLOCKED")}");
}

static string GetArg(string[] args, string flag, string defaultValue)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return defaultValue;
}

static void PrintUsage()
{
    Console.WriteLine("ThatsMyQuant CLI - Backtest & Walk-Forward Engine");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- backtest [options]     Run a single backtest session");
    Console.WriteLine("  dotnet run -- walkforward [options]  Run walk-forward analysis");
    Console.WriteLine("  dotnet run -- paper [options]        Run paper trading over N iterations");
    Console.WriteLine("  dotnet run -- hftbacktest [options]  Run mandatory L2 tick-replay backtest");
    Console.WriteLine("  dotnet run -- signalaudit [options]  Alpha predictiveness audit (IC, signal-alone PnL)");
    Console.WriteLine("  dotnet run -- backtest --days 90 ...  Benchmark short horizon");
    Console.WriteLine("  dotnet run -- backtest --days 365 ... Benchmark long horizon");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --symbol <sym>        Trading pair (default: BTC-USDT)");
    Console.WriteLine("  --symbols <csv>       Paper-trade symbol list (default: BTC-USDT,ETH-USDT)");
    Console.WriteLine("  --timeframe <tf>      Candle interval: 1m,5m,15m,1h,4h,1d (default: 1h)");
    Console.WriteLine("  --risk-profile <p>    Risk profile: Low, Moderate, Aggressive (default: Moderate)");
    Console.WriteLine("  --fast-screen <b>     Skip PBO/WF for faster screening (default: false)");
    Console.WriteLine("  --auto-deep-validate <b>  If fast-screen passes, run full validation (default: true)");
    Console.WriteLine("  --readiness-matrix <b>  Run strict all-profile readiness matrix (default: false)");
    Console.WriteLine("  --readiness-runs <n>  Consecutive runs per matrix cell (default: 3)");
    Console.WriteLine("  --stabilize-profiles <b>  Run profile stabilization loop via strict matrix (default: false)");
    Console.WriteLine("  --readiness-report <path>  JSON output for baseline/final readiness report");
    Console.WriteLine("  --readiness-fast-screen <b>  Matrix runs skip PBO/WF for speed (default: true)");
    Console.WriteLine("  --readiness-profile <csv>  Optional profile scope, e.g. Moderate");
    Console.WriteLine("  --readiness-days-scope <csv>  Optional horizon scope, e.g. 90,365");
    Console.WriteLine("  --capital <amount>    Starting capital (default: 100000)");
    Console.WriteLine("  --days <n>            Historical days to fetch (default: 365/180)");
    Console.WriteLine("  --benchmark-auto <b>  Backtest command auto-runs 90/365 leaderboard (default: false)");
    Console.WriteLine("  --windows <n>         Walk-forward windows (default: 6)");
    Console.WriteLine("  --train-ratio <r>     Train/test split ratio (default: 0.7)");
    Console.WriteLine("  --iterations <n>      Paper: number of scan iterations (default: 10)");
    Console.WriteLine("  --interval <secs>     Paper: seconds between iterations (default: 30)");
    Console.WriteLine("  --ticks-csv <path>    HFT: required tick data CSV path");
    Console.WriteLine("  --orderbooks-csv <path> HFT: required order-book CSV path");
    Console.WriteLine();
    Console.WriteLine("Config (appsettings.json):");
    Console.WriteLine("  Backtest:Performance   Replay/PBO logging, PBO/WFO parallelism (0=auto), EnableParallelWfoGridSearch");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- backtest --symbol ETH-USDT --timeframe 4h --days 120");
    Console.WriteLine("  dotnet run -- walkforward --symbol BTC-USDT --windows 8 --days 365");
    Console.WriteLine("  dotnet run -- paper --symbols BTC-USDT,ETH-USDT --iterations 20 --interval 60");
    Console.WriteLine("  dotnet run -- hftbacktest --symbol BTC-USDT --timeframe 5m --days 7 --ticks-csv data/ticks.csv --orderbooks-csv data/books.csv");
}

public sealed record StrictReadinessCell(
    string Profile,
    int Days,
    int Runs,
    bool StrictPass,
    decimal AvgPnl,
    double AvgSharpe,
    double AvgRr,
    bool HardGateAll,
    bool ParityAll,
    bool MlReadyAll,
    List<string> TopBlockers);

public sealed record StrictReadinessReport(
    DateTime GeneratedAtUtc,
    string Symbol,
    string Timeframe,
    int RunsPerCell,
    bool FastScreen,
    List<StrictReadinessCell> Cells,
    bool LiveReady);
