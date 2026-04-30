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
// Load Worker's appsettings.Paper.json so OKX:IsDemoMode + ConnectionStrings carry over.
// Credentials come from env vars (OKX__ApiKey etc.) injected by Run-HarvestFees.ps1.
var paperPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "QuantEngine.Worker", "appsettings.Paper.json");
if (File.Exists(paperPath))
    Microsoft.Extensions.Configuration.JsonConfigurationExtensions
        .AddJsonFile(builder.Configuration, paperPath, optional: true, reloadOnChange: false);
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddThatsMyQuantInfrastructure(builder.Configuration);
builder.Services.AddTransient<ISignalValidator, MarketHoursValidator>();
builder.Services.AddTransient<ISignalValidator, MinVolumeValidator>();
builder.Services.AddTransient<ISignalValidator, PriceSanityValidator>();
builder.Logging.ClearProviders();
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

    case "pairsbacktest":
        await RunPairsBacktestAsync(host.Services, args);
        break;

    case "fundingbacktest":
        await RunFundingBacktestAsync(host.Services, args);
        break;

    case "portfoliobacktest":
        await RunPortfolioBacktestAsync(host.Services, args);
        break;

    case "behaviorsim":
        await RunBehaviorSimulationAsync(host.Services, args);
        break;

    case "paper":
        await RunPaperAsync(host.Services, args);
        break;

    case "signalaudit":
        await RunSignalAuditAsync(host.Services, args);
        break;

    case "edgesweep":
        await RunEdgeSweepAsync(host.Services, args);
        break;

    case "pairsablation":
        await RunPairsAblationAsync(host.Services, args);
        break;

    case "harvest-fees":
        await RunHarvestFeesAsync(host.Services, args);
        break;

    case "analyze-edge":
        await RunAnalyzeEdgeAsync(args);
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

static async Task RunPairsBacktestAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var orchestrator = scope.ServiceProvider.GetRequiredService<BacktestOrchestrator>();

    var symbolA = GetArg(args, "--symbol-a", "BTC-USDT");
    var symbolB = GetArg(args, "--symbol-b", "ETH-USDT");
    var timeframe = GetArg(args, "--timeframe", "1h");
    var capital = decimal.Parse(GetArg(args, "--capital", "10000"), System.Globalization.CultureInfo.InvariantCulture);
    var daysBack = int.Parse(GetArg(args, "--days", "365"));

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine("=== ThatsMyQuant Pairs Backtest CLI ===");
    Console.WriteLine($"Symbols:    {symbolA} / {symbolB}");
    Console.WriteLine($"Timeframe:  {timeframe}");
    Console.WriteLine($"Period:     {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Capital:    {capital:N0}");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var s = await orchestrator.RunPairsSessionAsync(symbolA, symbolB, timeframe, start, end, capital);
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== Pairs Backtest Results ===");
    if (s.Status != BacktestSessionStatus.Completed)
    {
        Console.WriteLine($"Status:     {s.Status} — {s.SkipReason}");
        return;
    }
    Console.WriteLine($"Trades:     {s.TotalTrades}");
    Console.WriteLine($"PnL:        {s.TotalPnL:N2}  ({(capital > 0 ? s.TotalPnL / capital : 0):P2})");
    Console.WriteLine($"Sharpe:     {s.SharpeRatio:F3}");
    Console.WriteLine($"Sortino:    {s.SortinoRatio:F3}");
    Console.WriteLine($"Max DD:     {s.MaxDrawdownPercent:F2}%");
    Console.WriteLine($"Win Rate:   {s.WinRate:F1}%  ({s.WinningTrades}W / {s.LosingTrades}L)");
    Console.WriteLine($"Avg Win:    {s.AverageWin:N2}");
    Console.WriteLine($"Avg Loss:   {s.AverageLoss:N2}");
    Console.WriteLine($"Profit Fct: {s.ProfitFactor:F2}");
    Console.WriteLine($"Pass:       {(s.BacktestPass ? "YES" : "NO")}");
    Console.WriteLine($"\nCompleted in {sw.Elapsed.TotalSeconds:F1}s");
}

static async Task RunFundingBacktestAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.FundingAlphaBacktestRunner>();
    var histService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.IHistoricalMarketDataService>();

    var symbol = GetArg(args, "--symbol", "BTC-USDT");
    var timeframe = GetArg(args, "--timeframe", "1h");
    var capital = decimal.Parse(GetArg(args, "--capital", "100000"), System.Globalization.CultureInfo.InvariantCulture);
    var daysBack = int.Parse(GetArg(args, "--days", "365"));
    // Optional config overrides.
    var entryBps = decimal.Parse(GetArg(args, "--entry-bps", "3"), System.Globalization.CultureInfo.InvariantCulture);
    var tpPct = decimal.Parse(GetArg(args, "--tp-pct", "2"), System.Globalization.CultureInfo.InvariantCulture);
    var slPct = decimal.Parse(GetArg(args, "--sl-pct", "1.5"), System.Globalization.CultureInfo.InvariantCulture);
    var maxHold = int.Parse(GetArg(args, "--max-hold", "16"));

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine("=== Funding Alpha Backtest CLI ===");
    Console.WriteLine($"Symbol:       {symbol}");
    Console.WriteLine($"Timeframe:    {timeframe}");
    Console.WriteLine($"Period:       {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Capital:      {capital:N0}");
    Console.WriteLine($"Entry (bps):  ±{entryBps}  | TP: {tpPct}%  | SL: {slPct}%  | MaxHold: {maxHold}h");
    Console.WriteLine();

    var candles = await histService.GetHistoricalCandlesAsync(symbol, timeframe, start, end);
    if (candles.Count < 60)
    {
        Console.WriteLine($"Insufficient candles: {candles.Count}");
        return;
    }

    var useFlip = bool.Parse(GetArg(args, "--use-flip", "false"));

    var config = new ThatsMyQuant.Application.Strategy.FundingAlphaConfig(
        EntryThreshold: entryBps / 10000m,     // bps per 8h → fraction
        ExitThreshold: 0.00005m,
        TakeProfitPct: tpPct / 100m,
        StopLossPct: slPct / 100m,
        MaxHoldingBars: maxHold,
        UseFlipCatalyst: useFlip);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await runner.RunAsync(symbol, candles, config, capital);
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== Funding Alpha Results ===");
    Console.WriteLine($"Trades:       {result.TotalTrades}  ({result.WinningTrades}W / {result.LosingTrades}L)");
    Console.WriteLine($"Win Rate:     {result.WinRate:P1}");
    Console.WriteLine($"Sharpe:       {result.SharpeRatio:F3}");
    Console.WriteLine($"PnL:          {result.TotalPnl:N2}  ({(capital > 0 ? result.TotalPnl / capital : 0):P2})");
    Console.WriteLine($"Max DD:       {result.MaxDrawdownPct:P2}");
    Console.WriteLine($"Profit Fct:   {result.ProfitFactor:F2}");
    Console.WriteLine($"Funding Obs:  {result.FundingObservations}  (avg={result.AvgFunding:F5}  maxAbs={result.MaxAbsFunding:F5})");
    Console.WriteLine($"Entry Signals:{result.EntrySignals}");
    Console.WriteLine();

    if (result.Trades.Count > 0)
    {
        var byReason = result.Trades.GroupBy(t => t.ExitReason)
            .Select(g => new { Reason = g.Key, Count = g.Count(), Pnl = g.Sum(t => t.NetPnl) })
            .OrderByDescending(x => x.Count);
        Console.WriteLine("Exit-reason breakdown:");
        foreach (var r in byReason)
            Console.WriteLine($"  {r.Reason,-18}: {r.Count,3} trades | PnL: {r.Pnl,10:N2}");
    }

    Console.WriteLine($"\nCompleted in {sw.Elapsed.TotalSeconds:F1}s");
}

static async Task RunBehaviorSimulationAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var portfolioRunner = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.PortfolioBacktestRunner>();
    var simulator = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.ProMode.SystemBehaviorSimulator>();

    var capital = decimal.Parse(GetArg(args, "--capital", "100000"), System.Globalization.CultureInfo.InvariantCulture);
    var daysBack = int.Parse(GetArg(args, "--days", "365"));
    var riskProfile = GetArg(args, "--risk-profile", "Moderate");
    var profitOpts = scope.ServiceProvider.GetService<IOptions<ThatsMyQuant.Contracts.Options.ProfitMaximizationOptions>>()?.Value
        ?? new ThatsMyQuant.Contracts.Options.ProfitMaximizationOptions();
    var profile = profitOpts.Resolve(riskProfile);
    var leverage = Math.Clamp(profile.Leverage, 1m, 15m);

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine("=== System Behaviour Simulation (Phase D) ===");
    Console.WriteLine($"Capital:       {capital:N0}");
    Console.WriteLine($"Risk Profile:  {riskProfile} → leverage {leverage:F1}x");
    Console.WriteLine($"Period:        {start:yyyy-MM-dd} → {end:yyyy-MM-dd} ({daysBack}d)");
    Console.WriteLine();
    Console.WriteLine("Step 1: Running baseline portfolio backtest to capture per-strategy daily PnL...");
    var fundingCfg = new ThatsMyQuant.Application.Strategy.FundingAlphaConfig(
        EntryThreshold: 0.0001m, TakeProfitPct: 0.02m, StopLossPct: 0.015m, MaxHoldingBars: 16);
    var positions = new[]
    {
        new ThatsMyQuant.Application.Strategy.PortfolioPosition("TRX-XLM-Pair-4h", ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage, "TRX-USDT", "XLM-USDT", "4h", 0.30m, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition("SOL-AVAX-Pair-4h", ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage, "SOL-USDT", "AVAX-USDT", "4h", 0.20m, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition("SOL-LINK-Pair-4h", ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage, "SOL-USDT", "LINK-USDT", "4h", 0.15m, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition("LTC-Funding", ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha, "LTC-USDT", null, "1h", 0.15m, FundingConfig: fundingCfg, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition("DOGE-Funding", ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha, "DOGE-USDT", null, "1h", 0.10m, FundingConfig: fundingCfg, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition("AAVE-Funding", ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha, "AAVE-USDT", null, "1h", 0.10m, FundingConfig: fundingCfg, Leverage: leverage),
    };

    var portfolioResult = await portfolioRunner.RunAsync(positions, start, end, capital);

    Console.WriteLine($"  Baseline portfolio: PnL={portfolioResult.TotalPnl:N2} ({portfolioResult.TotalPnlPct:P2}) Sharpe={portfolioResult.PortfolioSharpe:F2} MaxDD={portfolioResult.MaxDrawdown:P2}");
    Console.WriteLine();
    Console.WriteLine("Step 2: Replaying with Pro Mode orchestration overlay (regime allocator + correlation guard + circuit breaker)...");

    // Convert portfolio equity curve differences to per-strategy daily PnL points.
    // NOTE: This simulator uses bucket allocation overlay on top of the backtest results.
    var strategyPnl = new Dictionary<string, IReadOnlyList<ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint>>(
        StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < portfolioResult.StrategyNames.Length; i++)
    {
        var name = portfolioResult.StrategyNames[i];
        var pts = new List<ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint>();
        for (int d = 0; d < portfolioResult.Dates.Length; d++)
        {
            // Equity-curve deltas are aggregate, but we need per-strategy. Use result trades
            // filtered — simpler: use aggregate daily diff distributed by position weight as proxy.
            // The runner doesn't expose per-strategy per-day directly, so we approximate by
            // scaling the equity delta by each strategy's relative contribution (its PnL / total).
        }
        // Proxy: distribute daily equity delta by strategy relative weight within the day.
        // For a first-cut sim we'll simply use equity-curve differences flat-split across strategies.
        strategyPnl[name] = pts;
    }

    // Simpler approach: use equity curve as a whole and simulate Pro Mode overlay on the aggregate.
    // Feed single "Portfolio" strategy series to simulator.
    var aggregate = new List<ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint>();
    for (int i = 0; i < portfolioResult.Dates.Length; i++)
    {
        decimal prevEq = i == 0 ? capital : portfolioResult.EquityCurve[i - 1];
        decimal diff = portfolioResult.EquityCurve[i] - prevEq;
        aggregate.Add(new ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint(portfolioResult.Dates[i], diff));
    }

    // Separate pair bucket vs funding bucket to enable correlation guard semantics.
    // We only have aggregate — so we approximate by splitting the daily return into two buckets
    // using the session's static 65/35 ratio (pairs/funding). Still meaningful for overlay behaviour.
    var pairSlice = aggregate.Select(p => new ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint(p.DateUtc, p.PnL * 0.65m)).ToList();
    var fundingSlice = aggregate.Select(p => new ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint(p.DateUtc, p.PnL * 0.35m)).ToList();

    var inputSeries = new Dictionary<string, IReadOnlyList<ThatsMyQuant.Application.Strategy.ProMode.DailyPnlPoint>>(StringComparer.OrdinalIgnoreCase)
    {
        ["Pairs-Aggregate-Pair"] = pairSlice,
        ["Funding-Aggregate-Funding"] = fundingSlice
    };

    var simResult = await simulator.SimulateAsync(
        inputSeries,
        regimeReferenceSymbol: "BTC-USDT",
        regimeTimeframe: "1h",
        startUtc: start, endUtc: end,
        totalCapital: capital);

    Console.WriteLine();
    Console.WriteLine("=== Phase D Behaviour Results ===");
    Console.WriteLine($"  BASELINE Sharpe / PnL / DD:   {portfolioResult.PortfolioSharpe:F2} / {portfolioResult.TotalPnl:N0} / {portfolioResult.MaxDrawdown:P2}");
    Console.WriteLine($"  PHASE D  Sharpe / PnL / DD:   {simResult.AnnualizedSharpe:F2} / {simResult.TotalPnl:N0} / {simResult.MaxDrawdown:P2}");
    Console.WriteLine($"  Δ Sharpe:  {simResult.AnnualizedSharpe - portfolioResult.PortfolioSharpe:+0.00;-0.00;0.00}");
    Console.WriteLine($"  Δ PnL:     {simResult.TotalPnl - portfolioResult.TotalPnl:+0.00;-0.00;0.00}");
    Console.WriteLine($"  Δ MaxDD:   {simResult.MaxDrawdown - portfolioResult.MaxDrawdown:+0.00%;-0.00%;0.00%}");
    Console.WriteLine($"  Circuit trips during sim: {simResult.CircuitTripCount}");
    Console.WriteLine($"  Trading days simulated:   {simResult.TradingDays}");
    Console.WriteLine();
    Console.WriteLine("  Regime distribution:");
    foreach (var kv in simResult.RegimeDistribution.OrderByDescending(k => k.Value))
        Console.WriteLine($"    {kv.Key,-10}: {kv.Value,4} days  ({(double)kv.Value / simResult.TradingDays:P0})");
    Console.WriteLine();
    Console.WriteLine("  Allocation by regime (last day of each):");
    foreach (var kv in simResult.RegimeDistribution.Keys)
    {
        var lastForRegime = simResult.DecisionLog.LastOrDefault(d => d.Regime == kv);
        if (lastForRegime is null) continue;
        var a = lastForRegime.Allocation;
        Console.WriteLine($"    {kv,-10}: pairs {a.PairsWeight:P0} · funding {a.FundingWeight:P0} · trend {a.TrendFollowWeight:P0} · CASH {a.CashWeight:P0}");
    }
    Console.WriteLine();
}

static async Task RunPortfolioBacktestAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.PortfolioBacktestRunner>();

    var capital = decimal.Parse(GetArg(args, "--capital", "100000"), System.Globalization.CultureInfo.InvariantCulture);
    var daysBack = int.Parse(GetArg(args, "--days", "90"));
    var riskProfile = GetArg(args, "--risk-profile", "Moderate");
    // Resolve risk profile from IOptions for leverage + DD budget.
    var profitOpts = scope.ServiceProvider.GetService<IOptions<ThatsMyQuant.Contracts.Options.ProfitMaximizationOptions>>()?.Value
        ?? new ThatsMyQuant.Contracts.Options.ProfitMaximizationOptions();
    var profile = profitOpts.Resolve(riskProfile);
    var leverage = Math.Clamp(profile.Leverage, 1m, 15m);  // hard cap regardless of config
    // Optional override for direct leverage testing (advanced users).
    var leverageOverride = GetArg(args, "--leverage", "");
    if (!string.IsNullOrWhiteSpace(leverageOverride) &&
        decimal.TryParse(leverageOverride, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var levOv))
    {
        leverage = Math.Clamp(levOv, 1m, 15m);
    }

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    // Toggle: --revised-pairs swaps SOL-LINK out for BCH-LTC (CLI ablation 2026-04-27 found
    // SOL-LINK has Sharpe -0.28 / -$642 in 365d backtest, dragging portfolio Sharpe from
    // ~0.85 to 0.39; BCH-LTC has positive Sharpe 0.65 in same period).
    bool useRevisedPairs = bool.Parse(GetArg(args, "--revised-pairs", "false"));
    // Toggle: --paper-config replicates the new appsettings.Paper.json pair list +
    // funding allocation exactly so backtest reflects what paper will run with after restart.
    bool usePaperConfig = bool.Parse(GetArg(args, "--paper-config", "false"));
    // Drop all funding legs (testing pairs-only thesis after 2026-04-27 ablation showed
    // funding contributes net -$32 / 395 trades at Aggressive 7x leverage).
    bool pairsOnly = bool.Parse(GetArg(args, "--pairs-only", "false"));
    // Tilt weights toward alpha-producing pairs (ADA-XRP, BCH-LTC) at expense of marginal ones.
    bool tiltAlpha = bool.Parse(GetArg(args, "--tilt-alpha", "false"));
    // Lower the pair entry threshold to multiply trade count. Default 2.0σ → 1.5σ
    // typically multiplies trades 2-3x; PnL scales but Sharpe may drop.
    double zEntryOverride = double.Parse(GetArg(args, "--zentry", "0"), System.Globalization.CultureInfo.InvariantCulture);

    // Kelly-weighted portfolio of 5 validated winners identified 2026-04-24.
    var fundingCfg1bp = new ThatsMyQuant.Application.Strategy.FundingAlphaConfig(
        EntryThreshold: 0.0001m, TakeProfitPct: 0.02m, StopLossPct: 0.015m, MaxHoldingBars: 16);
    // Portfolio rebalanced 2026-04-24 after portfolio backtest evidence:
    // LINK removed — funding scan showed Sharpe +4.95 IS but Sharpe -2.43 in combined backtest
    // (classic in-sample tuning trap, 138 trades at full horizon vs 34 trades in cherry-picked scan).
    // Remaining 4 positions all delivered positive in 365d portfolio run.
    // 2026-04-25: TRX/XLM weight reduced 0.30 → 0.20 to break single-pair PnL dominance
    // (was driving 94% of Aggressive-mode PnL). Pair allocation now equal across the three
    // 4h cointegrated pairs; ConcentrationGuard (25%/40%/60% caps) enforces this at runtime.
    // Paper config v4 (2026-04-28 — empirical loser pruning):
    //   v3 ablation showed 3 legs were portfolio drags:
    //     • SOL-AVAX: Sharpe -0.11, PnL -176 (12 trades) — dropped
    //     • FIL-AR:   Sharpe -0.29, PnL -457 (7 trades)  — dropped
    //     • SHIB-Funding: Sharpe -1.02, PnL -45.71 (126 trades) — dropped
    //   v4 keeps only legs with positive standalone Sharpe in 365d backtest:
    //     Pairs (52%): ADA-XRP 22, BCH-LTC 22, TRX-XLM 8
    //     Funding (27%): LTC 12, DOGE 10, AAVE 5
    //     Total: 79% — under-allocated; cash buffer absorbs drawdowns.
    //     Operator can rescale via --weighting invvol if desired.
    var positions = usePaperConfig
        ? new[]
        {
            new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "ADA-XRP-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
                SymbolA: "ADA-USDT", SymbolB: "XRP-USDT", Timeframe: "4h", CapitalWeight: 0.22m, Leverage: leverage),
            new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "BCH-LTC-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
                SymbolA: "BCH-USDT", SymbolB: "LTC-USDT", Timeframe: "4h", CapitalWeight: 0.22m, Leverage: leverage),
            new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "TRX-XLM-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
                SymbolA: "TRX-USDT", SymbolB: "XLM-USDT", Timeframe: "4h", CapitalWeight: 0.08m, Leverage: leverage),
            new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "LTC-Funding", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha,
                SymbolA: "LTC-USDT", SymbolB: null, Timeframe: "1h", CapitalWeight: 0.12m,
                FundingConfig: fundingCfg1bp, Leverage: leverage),
            new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "DOGE-Funding", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha,
                SymbolA: "DOGE-USDT", SymbolB: null, Timeframe: "1h", CapitalWeight: 0.10m,
                FundingConfig: fundingCfg1bp, Leverage: leverage),
            new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "AAVE-Funding", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha,
                SymbolA: "AAVE-USDT", SymbolB: null, Timeframe: "1h", CapitalWeight: 0.05m,
                FundingConfig: fundingCfg1bp, Leverage: leverage),
        }
        : new[]
    {
        new ThatsMyQuant.Application.Strategy.PortfolioPosition(
            Name: "TRX-XLM-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
            SymbolA: "TRX-USDT", SymbolB: "XLM-USDT", Timeframe: "4h", CapitalWeight: 0.20m,
            Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition(
            Name: "SOL-AVAX-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
            SymbolA: "SOL-USDT", SymbolB: "AVAX-USDT", Timeframe: "4h", CapitalWeight: 0.20m,
            Leverage: leverage),
        useRevisedPairs
            ? new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "BCH-LTC-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
                SymbolA: "BCH-USDT", SymbolB: "LTC-USDT", Timeframe: "4h", CapitalWeight: 0.20m,
                Leverage: leverage)
            : new ThatsMyQuant.Application.Strategy.PortfolioPosition(
                Name: "SOL-LINK-Pair-4h", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage,
                SymbolA: "SOL-USDT", SymbolB: "LINK-USDT", Timeframe: "4h", CapitalWeight: 0.20m,
                Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition(
            Name: "LTC-Funding", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha,
            SymbolA: "LTC-USDT", SymbolB: null, Timeframe: "1h", CapitalWeight: 0.15m,
            FundingConfig: fundingCfg1bp, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition(
            Name: "DOGE-Funding", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha,
            SymbolA: "DOGE-USDT", SymbolB: null, Timeframe: "1h", CapitalWeight: 0.13m,
            FundingConfig: fundingCfg1bp, Leverage: leverage),
        new ThatsMyQuant.Application.Strategy.PortfolioPosition(
            Name: "AAVE-Funding", Type: ThatsMyQuant.Application.Strategy.PortfolioPositionType.FundingAlpha,
            SymbolA: "AAVE-USDT", SymbolB: null, Timeframe: "1h", CapitalWeight: 0.12m,
            FundingConfig: fundingCfg1bp, Leverage: leverage),
    };

    Console.WriteLine("=== Portfolio Backtest CLI ===");
    Console.WriteLine($"Positions:    {positions.Length}");
    Console.WriteLine($"Capital:      {capital:N0}");
    Console.WriteLine($"Risk Profile: {riskProfile}  → Leverage {leverage:F1}x · DD budget {profile.MaxDrawdownBudget:P1} · throttle at {profile.DrawdownThrottleFraction:P0} of budget");
    Console.WriteLine($"Period:       {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine();
    foreach (var p in positions)
        Console.WriteLine($"  - {p.Name,-22} {p.Type,-18} weight={p.CapitalWeight:P0}  {p.SymbolA}/{p.SymbolB ?? "-"} @ {p.Timeframe}");
    Console.WriteLine();

    // Weighting mode: fixed (default) | invvol | hrp
    var weightingArg = GetArg(args, "--weighting", "fixed").ToLowerInvariant();
    var weightingMode = weightingArg switch
    {
        "invvol" or "inverse-vol" or "inversevolatility" => ThatsMyQuant.Application.Strategy.PortfolioWeightingMode.InverseVolatility,
        "hrp" or "hierarchicalriskparity" => ThatsMyQuant.Application.Strategy.PortfolioWeightingMode.HierarchicalRiskParity,
        "bl" or "blacklitterman" => ThatsMyQuant.Application.Strategy.PortfolioWeightingMode.BlackLitterman,
        _ => ThatsMyQuant.Application.Strategy.PortfolioWeightingMode.Fixed,
    };
    Console.WriteLine($"Weighting:    {weightingMode}");
    Console.WriteLine();

    if (pairsOnly)
    {
        positions = positions.Where(p => p.Type == ThatsMyQuant.Application.Strategy.PortfolioPositionType.PairsArbitrage).ToArray();
        Console.WriteLine($"PairsOnly mode: dropped funding legs, trading {positions.Length} pair strategies.");
    }
    if (zEntryOverride > 0)
    {
        var optAccessor = scope.ServiceProvider.GetRequiredService<IOptions<ThatsMyQuant.Contracts.Options.PairsTradingOptions>>();
        optAccessor.Value.EntryZScoreThreshold = zEntryOverride;
        Console.WriteLine($"ZScoreEntry override: {zEntryOverride}");
    }
    bool blockStopHunt = bool.Parse(GetArg(args, "--block-stop-hunt", "false"));
    bool useGarch = bool.Parse(GetArg(args, "--use-garch", "false"));
    if (blockStopHunt || useGarch)
    {
        var optAccessor = scope.ServiceProvider.GetRequiredService<IOptions<ThatsMyQuant.Contracts.Options.PairsTradingOptions>>();
        if (blockStopHunt) optAccessor.Value.BlockEntryOnStopHunt = true;
        if (useGarch) optAccessor.Value.UseGarchForwardVolSizing = true;
        Console.WriteLine($"Wired modules: BlockStopHunt={blockStopHunt}, GarchForwardVol={useGarch}");
    }
    if (tiltAlpha)
    {
        // Reweight alpha-producers (ADA-XRP, BCH-LTC) up and marginals down.
        positions = positions.Select(p => p.Name switch
        {
            "ADA-XRP-Pair-4h" => p with { CapitalWeight = 0.30m },
            "BCH-LTC-Pair-4h" => p with { CapitalWeight = 0.30m },
            "FIL-AR-Pair-4h"  => p with { CapitalWeight = 0.20m },
            "TRX-XLM-Pair-4h" => p with { CapitalWeight = 0.10m },
            "SOL-AVAX-Pair-4h" => p with { CapitalWeight = 0.05m },
            _ => p
        }).ToArray();
        Console.WriteLine($"Tilt-alpha applied: ADA-XRP/BCH-LTC=30%, FIL-AR=20%, TRX-XLM=10%, SOL-AVAX=5%.");
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await runner.RunAsync(positions, start, end, capital, weightingMode);
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== Per-Strategy Results ===");
    Console.WriteLine($"{"Name",-22} {"Trades",7} {"WR",8} {"Sharpe",8} {"PnL",12} {"PF",6}");
    foreach (var s in result.StrategyResults)
        Console.WriteLine($"{s.Name,-22} {s.Trades,7} {s.WinRate,8:P1} {s.SharpeRatio,8:F2} {s.NetPnl,12:N2} {s.ProfitFactor,6:F2}");

    Console.WriteLine();
    Console.WriteLine("=== Portfolio Aggregate ===");
    Console.WriteLine($"Total Capital:  {result.TotalCapital:N2}");
    Console.WriteLine($"Final Equity:   {result.FinalEquity:N2}");
    Console.WriteLine($"Total PnL:      {result.TotalPnl:N2}  ({result.TotalPnlPct:P2})");
    Console.WriteLine($"Portfolio Shp:  {result.PortfolioSharpe:F3}");
    Console.WriteLine($"Max Drawdown:   {result.MaxDrawdown:P2}");
    Console.WriteLine($"Trading Days:   {result.TradingDays}");

    Console.WriteLine();
    Console.WriteLine("=== Correlation Matrix ===");
    Console.Write($"{"",-22}");
    foreach (var n in result.StrategyNames) Console.Write($"{n.Substring(0, Math.Min(10, n.Length)),11}");
    Console.WriteLine();
    for (int i = 0; i < result.StrategyNames.Length; i++)
    {
        Console.Write($"{result.StrategyNames[i],-22}");
        for (int j = 0; j < result.StrategyNames.Length; j++)
            Console.Write($"{result.CorrelationMatrix[i, j],11:F3}");
        Console.WriteLine();
    }

    double offDiagAvg = 0; int count = 0;
    for (int i = 0; i < result.StrategyNames.Length; i++)
    for (int j = 0; j < result.StrategyNames.Length; j++)
        if (i != j) { offDiagAvg += result.CorrelationMatrix[i, j]; count++; }
    if (count > 0) offDiagAvg /= count;
    Console.WriteLine();
    Console.WriteLine($"Average off-diagonal correlation: {offDiagAvg:F3}");
    Console.WriteLine($"  ≤0.30 = true diversification; 0.30–0.60 = moderate; ≥0.60 = crowded");
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

static async Task RunPairsAblationAsync(IServiceProvider services, string[] args)
{
    using var scope = services.CreateScope();
    var histService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.IHistoricalMarketDataService>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

    var symbolA = GetArg(args, "--symbol-a", "BCH-USDT");
    var symbolB = GetArg(args, "--symbol-b", "LTC-USDT");
    var timeframe = GetArg(args, "--timeframe", "4h");
    var capital = decimal.Parse(GetArg(args, "--capital", "10000"), System.Globalization.CultureInfo.InvariantCulture);
    var daysBack = int.Parse(GetArg(args, "--days", "365"));
    var bootstrapReps = int.Parse(GetArg(args, "--bootstrap", "1000"));

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine("=== Pairs Gate Ablation + Validation Toolkit ===");
    Console.WriteLine($"Symbols:    {symbolA} / {symbolB}");
    Console.WriteLine($"Timeframe:  {timeframe}");
    Console.WriteLine($"Period:     {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Capital:    {capital:N0}");
    Console.WriteLine();

    var candlesA = await histService.GetHistoricalCandlesAsync(symbolA, timeframe, start, end);
    var candlesB = await histService.GetHistoricalCandlesAsync(symbolB, timeframe, start, end);
    Console.WriteLine($"Loaded candles: A={candlesA.Count}, B={candlesB.Count}");

    if (candlesA.Count < 100 || candlesB.Count < 100)
    {
        Console.WriteLine("Insufficient data, aborting.");
        return;
    }

    // [F1/F2/F3 unified-plan ablation] Five configurations measured side-by-side:
    //  • "Baseline":  every Sprint-2/3 + F1/F2/F3 gate OFF — pre-fixes reference.
    //  • "Production": all foundation fixes ON (current PairsTradingOptions defaults).
    //  • "Prod -F1":  Production minus regime gate — isolates F1's contribution.
    //  • "Prod -F2":  Production minus OU-optimal stopping — isolates F2.
    //  • "Prod -F3":  Production minus confluence floors — isolates F3.
    ThatsMyQuant.Contracts.Options.PairsTradingOptions BuildOptions(
        bool requireRangingRegime, bool useOuOptimalStopping, bool applyConfluenceFloors,
        bool spreadVelocityOn) => new()
    {
        SymbolA = symbolA,
        SymbolB = symbolB,
        UseKalmanHedgeRatio = true,
        KalmanProcessVariance = 1e-4,
        KalmanInitialVariance = 1.0,
        CointegrationLookbackBars = 180,
        ZScoreWindow = 30,
        MinCorrelation = 0.30,
        MinAbsCorrelation = 0.30,
        MaxHedgeRatio = 15.00m,
        MinHedgeRatio = 0.10m,
        MaxHurstExponent = 0.55,
        RequireRangingRegime = requireRangingRegime,
        ApplyConfluenceFloors = applyConfluenceFloors,
        UseOuOptimalStopping = useOuOptimalStopping,
        SpreadVelocityRejectThreshold = spreadVelocityOn ? 0.35 : 999.0,
    };

    ThatsMyQuant.Contracts.Models.BacktestResult RunCfg(
        bool req, bool ou, bool conf, bool vel, string label)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(BuildOptions(req, ou, conf, vel));
        var run = new ThatsMyQuant.Application.Strategy.PairsBacktestRunner(
            loggerFactory.CreateLogger<ThatsMyQuant.Application.Strategy.PairsBacktestRunner>(), opts);
        var r = run.Run(candlesA, candlesB, capital);
        Console.WriteLine($"  [{label,-12}] trades={r.TotalTrades,4}  Sharpe={r.SharpeRatio,6:F3}  PnL={r.TotalPnL,10:N2}");
        return r;
    }

    Console.WriteLine();
    Console.WriteLine("Running ablation matrix...");
    var resultBaseline = RunCfg(req: false, ou: false, conf: false, vel: false, "Baseline");
    var resultOn       = RunCfg(req: true,  ou: true,  conf: true,  vel: true,  "Production");
    var resultMinusF1  = RunCfg(req: false, ou: true,  conf: true,  vel: true,  "Prod -F1");
    var resultMinusF2  = RunCfg(req: true,  ou: false, conf: true,  vel: true,  "Prod -F2");
    var resultMinusF3  = RunCfg(req: true,  ou: true,  conf: false, vel: true,  "Prod -F3");
    var resultOff = resultBaseline; // back-compat alias for the existing report block

    // Helper to extract per-trade pnl as fractions of capital for stats.
    static double[] ToReturns(System.Collections.Generic.IReadOnlyList<ThatsMyQuant.Contracts.Models.BacktestTradeResult> trades, decimal capital)
    {
        var arr = new double[trades.Count];
        for (int i = 0; i < trades.Count; i++) arr[i] = capital > 0 ? (double)(trades[i].PnL / capital) : 0;
        return arr;
    }
    var retsOn = ToReturns(resultOn.Trades, capital);
    var retsOff = ToReturns(resultOff.Trades, capital);

    // -------- Validation toolkit --------
    static (double psr, double dsr, double sharpeLow, double sharpeHigh) ComputeStats(double[] rets, int reps)
    {
        if (rets.Length < 4) return (0, 0, 0, 0);
        double psr = ThatsMyQuant.Application.Strategy.DeflatedSharpeRatio.ProbabilisticSharpe(rets);
        // Two trials = ON vs OFF — small selection-bias correction.
        var dsrResult = ThatsMyQuant.Application.Strategy.DeflatedSharpeRatio.Compute(
            rets, numTrials: 2, varianceOfTrialSharpes: 0.5);
        var ci = ThatsMyQuant.Application.Strategy.BlockBootstrapSharpe.Compute(
            rets, numBootstrap: reps, seed: 7);
        return (psr, dsrResult.DeflatedSharpe, ci.LowerBound, ci.UpperBound);
    }

    var (psrOn, dsrOn, ciLowOn, ciHighOn) = ComputeStats(retsOn, bootstrapReps);
    var (psrOff, dsrOff, ciLowOff, ciHighOff) = ComputeStats(retsOff, bootstrapReps);

    // -------- Side-by-side report --------
    Console.WriteLine();
    Console.WriteLine("=== Ablation Results (Production Defaults vs Pre-Sprint Baseline) ===");
    Console.WriteLine($"{"Metric",-20} {"Production",-15} {"All Off",-15} {"Delta",-15}");
    Console.WriteLine(new string('-', 65));
    void Row(string label, string on, string off, string delta) =>
        Console.WriteLine($"{label,-20} {on,-15} {off,-15} {delta,-15}");
    Row("Trades", resultOn.TotalTrades.ToString(), resultOff.TotalTrades.ToString(),
        (resultOn.TotalTrades - resultOff.TotalTrades).ToString("+#;-#;0"));
    Row("PnL", resultOn.TotalPnL.ToString("N2"), resultOff.TotalPnL.ToString("N2"),
        (resultOn.TotalPnL - resultOff.TotalPnL).ToString("+N2;-N2;0"));
    Row("Sharpe", resultOn.SharpeRatio.ToString("F3"), resultOff.SharpeRatio.ToString("F3"),
        (resultOn.SharpeRatio - resultOff.SharpeRatio).ToString("+F3;-F3;0"));
    Row("Sortino", resultOn.SortinoRatio.ToString("F3"), resultOff.SortinoRatio.ToString("F3"),
        (resultOn.SortinoRatio - resultOff.SortinoRatio).ToString("+F3;-F3;0"));
    Row("Win Rate %", resultOn.WinRate.ToString("F1"), resultOff.WinRate.ToString("F1"),
        (resultOn.WinRate - resultOff.WinRate).ToString("+F1;-F1;0"));
    Row("Profit Factor", resultOn.ProfitFactor.ToString("F2"), resultOff.ProfitFactor.ToString("F2"),
        (resultOn.ProfitFactor - resultOff.ProfitFactor).ToString("+F2;-F2;0"));
    Row("Max DD %", resultOn.MaxDrawdownPercent.ToString("F2"), resultOff.MaxDrawdownPercent.ToString("F2"),
        (resultOn.MaxDrawdownPercent - resultOff.MaxDrawdownPercent).ToString("+F2;-F2;0"));

    Console.WriteLine();
    Console.WriteLine("=== Per-Fix Marginal Contribution (Production vs Production-minus-Fix) ===");
    void FixRow(string label, ThatsMyQuant.Contracts.Models.BacktestResult minus)
    {
        double dSharpe = resultOn.SharpeRatio - minus.SharpeRatio;
        int dTrades = resultOn.TotalTrades - minus.TotalTrades;
        decimal dPnl = resultOn.TotalPnL - minus.TotalPnL;
        Console.WriteLine($"  {label,-12} Sharpe Δ={dSharpe,+7:F3}  Trades Δ={dTrades,+5}  PnL Δ={dPnl,+12:N2}");
    }
    FixRow("F1 (regime)",   resultMinusF1);
    FixRow("F2 (OU exit)",  resultMinusF2);
    FixRow("F3 (confluence)", resultMinusF3);

    Console.WriteLine();
    Console.WriteLine("=== Validation Toolkit (Bailey-Lopez de Prado) ===");
    Row("PSR", psrOn.ToString("P1"), psrOff.ToString("P1"), "");
    Row("DSR (2 trials)", dsrOn.ToString("P1"), dsrOff.ToString("P1"), "");
    Row("Sharpe 95% CI Low", ciLowOn.ToString("F3"), ciLowOff.ToString("F3"), "");
    Row("Sharpe 95% CI High", ciHighOn.ToString("F3"), ciHighOff.ToString("F3"), "");

    Console.WriteLine();
    Console.WriteLine("=== Paper Restart Gate ===");
    bool sharpePass = resultOn.SharpeRatio >= 0.5;
    bool psrPass = psrOn >= 0.95;
    bool dsrPass = dsrOn >= 0.95;
    bool ciPass = ciLowOn >= 0.0;
    bool noRegression = resultOn.SharpeRatio >= 0.9 * resultOff.SharpeRatio;
    Console.WriteLine($"  Sharpe ≥ 0.5 (positive edge):     {(sharpePass ? "PASS" : "FAIL")}  ({resultOn.SharpeRatio:F3})");
    Console.WriteLine($"  PSR ≥ 0.95 (vs zero):             {(psrPass ? "PASS" : "FAIL")}  ({psrOn:P1})");
    Console.WriteLine($"  DSR ≥ 0.95 (selection-corrected): {(dsrPass ? "PASS" : "FAIL")}  ({dsrOn:P1})");
    Console.WriteLine($"  Bootstrap CI lower ≥ 0:           {(ciPass ? "PASS" : "FAIL")}  ({ciLowOn:F3})");
    Console.WriteLine($"  No regression vs gates-OFF:       {(noRegression ? "PASS" : "FAIL")}  (ON {resultOn.SharpeRatio:F3} vs OFF {resultOff.SharpeRatio:F3})");
    bool allPass = sharpePass && psrPass && dsrPass && ciPass && noRegression;
    Console.WriteLine();
    Console.WriteLine($"  OVERALL: {(allPass ? "✅ READY for paper restart" : "❌ NOT READY — investigate failures above")}");
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

/// <summary>
/// Edge Breadth Sweep — runs FundingAlphaBacktestRunner across N symbols and
/// PairsBacktestRunner across NC2 pair combinations, applying an IS/OOS split so
/// candidates that survive must pass BOTH windows. Outputs a ranked candidate list
/// to disk for operator review (NEVER auto-promotes into Strategy:* config).
///
/// Usage:
///   dotnet run -- edgesweep --days 365 --oos-days 90
///                            --symbols BTC-USDT,ETH-USDT,SOL-USDT,...   (default: 30 OKX large-caps)
///                            --funding-only        (skip pairs)
///                            --pairs-only          (skip funding)
///                            --min-pf 1.20 --min-oos-pf 1.10 --min-oos-sharpe 0.5 --min-oos-wr 0.45
/// Output:  %LOCALAPPDATA%\ThatsMyQuant\edge_sweep_candidates.json
/// </summary>
static async Task RunEdgeSweepAsync(IServiceProvider services, string[] args)
{
    var daysBack = int.Parse(GetArg(args, "--days", "365"));
    var oosDays = int.Parse(GetArg(args, "--oos-days", "90"));
    var capital = decimal.Parse(GetArg(args, "--capital", "100000"), System.Globalization.CultureInfo.InvariantCulture);
    var fundingTimeframe = GetArg(args, "--funding-timeframe", "1h");
    var pairsTimeframe = GetArg(args, "--pairs-timeframe", "4h");
    var minPf = decimal.Parse(GetArg(args, "--min-pf", "1.20"), System.Globalization.CultureInfo.InvariantCulture);
    var minOosPf = decimal.Parse(GetArg(args, "--min-oos-pf", "1.10"), System.Globalization.CultureInfo.InvariantCulture);
    var minOosSharpe = double.Parse(GetArg(args, "--min-oos-sharpe", "0.5"), System.Globalization.CultureInfo.InvariantCulture);
    var minOosWr = decimal.Parse(GetArg(args, "--min-oos-wr", "0.45"), System.Globalization.CultureInfo.InvariantCulture);
    var fundingOnly = args.Contains("--funding-only");
    var pairsOnly = args.Contains("--pairs-only");

    var symbolsCsv = GetArg(args, "--symbols",
        "BTC-USDT,ETH-USDT,SOL-USDT,AVAX-USDT,LINK-USDT,LTC-USDT,DOGE-USDT,XRP-USDT,ADA-USDT,AAVE-USDT," +
        "ATOM-USDT,DOT-USDT,NEAR-USDT,APT-USDT,SUI-USDT,ARB-USDT,OP-USDT,INJ-USDT,FIL-USDT,TRX-USDT," +
        "XLM-USDT,BCH-USDT,UNI-USDT,ETC-USDT,MATIC-USDT,ALGO-USDT,FTM-USDT,SAND-USDT,GALA-USDT,EGLD-USDT");
    var symbols = symbolsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var endUtc = DateTime.UtcNow;
    var oosStart = endUtc.AddDays(-oosDays);
    var isStart = endUtc.AddDays(-daysBack);
    var perCapital = capital / Math.Max(symbols.Length, 1);

    Console.WriteLine("=== Edge Breadth Sweep ===");
    Console.WriteLine($"Universe:       {symbols.Length} symbols");
    Console.WriteLine($"IS window:      {isStart:yyyy-MM-dd} → {oosStart:yyyy-MM-dd} ({daysBack - oosDays}d)");
    Console.WriteLine($"OOS window:     {oosStart:yyyy-MM-dd} → {endUtc:yyyy-MM-dd} ({oosDays}d)");
    Console.WriteLine($"Funding TF:     {fundingTimeframe}   Pairs TF: {pairsTimeframe}");
    Console.WriteLine($"Per-symbol cap: {perCapital:N0}");
    Console.WriteLine($"Gates:          IS PF≥{minPf}  AND  OOS PF≥{minOosPf} AND OOS Sharpe≥{minOosSharpe:F2} AND OOS WR≥{minOosWr:P0}");
    Console.WriteLine();

    using var scope = services.CreateScope();
    var fundingRunner = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.FundingAlphaBacktestRunner>();
    var orchestrator = scope.ServiceProvider.GetRequiredService<BacktestOrchestrator>();
    var histService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.IHistoricalMarketDataService>();

    var fundingCandidates = new List<object>();
    var pairCandidates = new List<object>();
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // ------- FUNDING SWEEP -------
    if (!pairsOnly)
    {
        Console.WriteLine("--- Funding sweep ---");
        var fcfg = new ThatsMyQuant.Application.Strategy.FundingAlphaConfig(
            EntryThreshold: 0.0001m, ExitThreshold: 0.00005m,
            TakeProfitPct: 0.02m, StopLossPct: 0.015m, MaxHoldingBars: 16);

        int idx = 0;
        foreach (var sym in symbols)
        {
            idx++;
            try
            {
                var isCandles = await histService.GetHistoricalCandlesAsync(sym, fundingTimeframe, isStart, oosStart);
                var oosCandles = await histService.GetHistoricalCandlesAsync(sym, fundingTimeframe, oosStart, endUtc);
                if (isCandles.Count < 100 || oosCandles.Count < 60)
                {
                    Console.WriteLine($"  [{idx,2}/{symbols.Length}] {sym,-12} skipped (insufficient candles IS={isCandles.Count} OOS={oosCandles.Count})");
                    continue;
                }
                var isResult = await fundingRunner.RunAsync(sym, isCandles, fcfg, perCapital);
                var oosResult = await fundingRunner.RunAsync(sym, oosCandles, fcfg, perCapital);

                var passes = (decimal)isResult.ProfitFactor >= minPf
                    && (decimal)oosResult.ProfitFactor >= minOosPf
                    && oosResult.SharpeRatio >= minOosSharpe
                    && (decimal)oosResult.WinRate >= minOosWr;

                var marker = passes ? "✓ PASS" : "  fail";
                Console.WriteLine(
                    $"  [{idx,2}/{symbols.Length}] {sym,-12} IS PF={isResult.ProfitFactor,5:F2} ({isResult.TotalTrades,3}t) | OOS PF={oosResult.ProfitFactor,5:F2} Sharpe={oosResult.SharpeRatio,5:F2} WR={oosResult.WinRate,5:P0} ({oosResult.TotalTrades,3}t) {marker}");

                if (passes)
                {
                    fundingCandidates.Add(new
                    {
                        symbol = sym,
                        isWindow = new { trades = isResult.TotalTrades, pf = Math.Round(isResult.ProfitFactor, 2), sharpe = Math.Round(isResult.SharpeRatio, 2), wr = Math.Round(isResult.WinRate, 3) },
                        oosWindow = new { trades = oosResult.TotalTrades, pf = Math.Round(oosResult.ProfitFactor, 2), sharpe = Math.Round(oosResult.SharpeRatio, 2), wr = Math.Round(oosResult.WinRate, 3) },
                        recommendedWeight = Math.Round(Math.Min(0.20, oosResult.SharpeRatio * 0.05), 3)
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{idx,2}/{symbols.Length}] {sym,-12} ERROR: {ex.Message}");
            }
        }
        Console.WriteLine($"Funding sweep done: {fundingCandidates.Count} candidates passed gates.");
        Console.WriteLine();
    }

    // ------- PAIRS SWEEP (NC2 combinations) -------
    if (!fundingOnly)
    {
        Console.WriteLine("--- Pairs sweep ---");
        var pairCount = symbols.Length * (symbols.Length - 1) / 2;
        Console.WriteLine($"Testing {pairCount} pair combinations on {pairsTimeframe}...");
        int tested = 0, admitted = 0;
        var pairCapital = capital * 0.10m;  // small notional so PF/Sharpe are comparable

        for (int i = 0; i < symbols.Length; i++)
        {
            for (int j = i + 1; j < symbols.Length; j++)
            {
                tested++;
                var a = symbols[i]; var b = symbols[j];
                try
                {
                    // Use full window for pairs (cointegration needs ≥250 bars on 4h)
                    var fullS = await orchestrator.RunPairsSessionAsync(a, b, pairsTimeframe, isStart, endUtc, pairCapital);
                    if (fullS.Status != BacktestSessionStatus.Completed || fullS.TotalTrades < 6) continue;

                    // Re-run on OOS only
                    var oosS = await orchestrator.RunPairsSessionAsync(a, b, pairsTimeframe, oosStart, endUtc, pairCapital);
                    if (oosS.Status != BacktestSessionStatus.Completed) continue;

                    var passes = (decimal)fullS.ProfitFactor >= minPf
                        && (decimal)oosS.ProfitFactor >= minOosPf
                        && oosS.SharpeRatio >= minOosSharpe;

                    if (passes)
                    {
                        admitted++;
                        Console.WriteLine($"  {a}/{b,-22} ✓ full PF={fullS.ProfitFactor:F2}  OOS PF={oosS.ProfitFactor:F2} Sharpe={oosS.SharpeRatio:F2} ({oosS.TotalTrades}t)");
                        pairCandidates.Add(new
                        {
                            pair = $"{a}:{b}",
                            full = new { trades = fullS.TotalTrades, pf = Math.Round(fullS.ProfitFactor, 2), sharpe = Math.Round(fullS.SharpeRatio, 2), winRate = Math.Round(fullS.WinRate, 1) },
                            oos = new { trades = oosS.TotalTrades, pf = Math.Round(oosS.ProfitFactor, 2), sharpe = Math.Round(oosS.SharpeRatio, 2), winRate = Math.Round(oosS.WinRate, 1) },
                            recommendedWeight = Math.Round(Math.Min(0.15, oosS.SharpeRatio * 0.05), 3)
                        });
                    }

                    if (tested % 25 == 0)
                        Console.WriteLine($"  ... progress {tested}/{pairCount} ({admitted} admitted so far)");
                }
                catch (Exception ex)
                {
                    if (tested % 50 == 0) Console.WriteLine($"  [{tested}/{pairCount}] {a}/{b} skipped: {ex.Message}");
                }
            }
        }
        Console.WriteLine($"Pairs sweep done: {admitted} candidates / {tested} tested.");
        Console.WriteLine();
    }

    sw.Stop();

    // Persist results.
    var report = new
    {
        generatedUtc = DateTime.UtcNow,
        elapsedSec = Math.Round(sw.Elapsed.TotalSeconds, 1),
        windows = new { isFromUtc = isStart, oosFromUtc = oosStart, endUtc },
        gates = new { minPf, minOosPf, minOosSharpe, minOosWr },
        fundingCandidates,
        pairCandidates
    };
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant");
    Directory.CreateDirectory(dir);
    var outPath = Path.Combine(dir, "edge_sweep_candidates.json");
    var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    var tmp = outPath + ".tmp";
    await File.WriteAllTextAsync(tmp, json);
    File.Move(tmp, outPath, overwrite: true);

    Console.WriteLine("=== Edge Sweep Summary ===");
    Console.WriteLine($"Funding candidates: {fundingCandidates.Count}");
    Console.WriteLine($"Pairs candidates:   {pairCandidates.Count}");
    Console.WriteLine($"Elapsed:            {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"Report:             {outPath}");
    Console.WriteLine();
    Console.WriteLine("NEXT STEP: review candidates JSON, pick winners, manually edit:");
    Console.WriteLine("  - Funding:AllocationCsv  (in appsettings.Paper.json)");
    Console.WriteLine("  - Strategy:PairsTrading:AllowedPairsCsv");
}

static async Task RunAnalyzeEdgeAsync(string[] args)
{
    // Reads paper_portfolio.json and reports per-symbol statistical significance:
    // - t-statistic vs zero
    // - bootstrapped Sharpe 95% CI (per-trade)
    // - Bonferroni-corrected required |t| for the universe size
    // - Deflated Sharpe Ratio probability (Bailey & López de Prado 2014) — adjusts
    //   for both the multiple-comparison family and the higher moments of the
    //   return distribution.
    var portfolioArg = args.FirstOrDefault(a => a.StartsWith("--portfolio=", StringComparison.OrdinalIgnoreCase));
    var feeBpsArg    = args.FirstOrDefault(a => a.StartsWith("--fee-bps-leg=", StringComparison.OrdinalIgnoreCase));
    string portfolioPath = portfolioArg is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant", "paper_portfolio.json")
        : portfolioArg.Substring("--portfolio=".Length);
    decimal feeBpsPerLeg = feeBpsArg is null ? 5m : decimal.Parse(feeBpsArg.Substring("--fee-bps-leg=".Length), System.Globalization.CultureInfo.InvariantCulture);

    if (!File.Exists(portfolioPath))
    {
        Console.WriteLine($"[analyze-edge] portfolio file not found: {portfolioPath}");
        return;
    }
    var json = await File.ReadAllTextAsync(portfolioPath);
    using var doc = System.Text.Json.JsonDocument.Parse(json);
    if (!doc.RootElement.TryGetProperty("ClosedPositions", out var closed)) { Console.WriteLine("No closed positions."); return; }

    var bySymbol = new Dictionary<string, List<(double Net, double Gross, decimal? RealFee)>>(StringComparer.OrdinalIgnoreCase);
    foreach (var t in closed.EnumerateArray())
    {
        var sym = t.GetProperty("Symbol").GetString() ?? "";
        var entry = t.GetProperty("EntryPrice").GetDecimal();
        var exit = t.GetProperty("CurrentPrice").GetDecimal();
        var qty = t.GetProperty("Quantity").GetDecimal();
        var side = t.GetProperty("Side").GetInt32();
        if (entry == 0) continue;
        var grossD = side == 0 ? (exit - entry) * qty : (entry - exit) * qty;
        decimal? rfp = null;
        if (t.TryGetProperty("RealFeePaid", out var rfpEl) && rfpEl.ValueKind == System.Text.Json.JsonValueKind.Number)
            rfp = rfpEl.GetDecimal();
        var assumedFee = (entry + exit) * qty * (feeBpsPerLeg / 10_000m);
        var feeApplied = rfp ?? assumedFee;
        var net = grossD - feeApplied;
        if (!bySymbol.ContainsKey(sym)) bySymbol[sym] = new();
        bySymbol[sym].Add(((double)net, (double)grossD, rfp));
    }

    var symCount = bySymbol.Count;
    var requiredAbsT = ThatsMyQuant.Application.Strategy.StatisticalSignificance.BonferroniRequiredAbsT(symCount, 0.05);

    Console.WriteLine($"=== Per-symbol edge significance (universe size N={symCount}, Bonferroni |t|>{requiredAbsT:F2} required) ===");
    Console.WriteLine($"{"Symbol",-10} {"N",4} {"Net$",8} {"Mean$",8} {"t-stat",7} {"SR/trd",7} {"95%CI low",10} {"95%CI hi",10} {"DSR p>0",8} {"Verdict"}");
    foreach (var kv in bySymbol.OrderBy(k => k.Key, StringComparer.Ordinal))
    {
        var nets = kv.Value.Select(v => v.Net).ToList();
        if (nets.Count < 2) { Console.WriteLine($"{kv.Key,-10} {nets.Count,4}  (insufficient sample)"); continue; }
        var total = nets.Sum();
        var mean = nets.Average();
        var t = ThatsMyQuant.Application.Strategy.StatisticalSignificance.TStatisticVsZero(nets);
        var sr = ThatsMyQuant.Application.Strategy.StatisticalSignificance.SharpePerTrade(nets);
        var (lo, hi, _) = ThatsMyQuant.Application.Strategy.StatisticalSignificance.BootstrapSharpeCI(nets, iterations: 5000);
        var dsr = ThatsMyQuant.Application.Strategy.StatisticalSignificance.DeflatedSharpeRatioProbability(nets, symCount, sr);
        string verdict;
        if (Math.Abs(t) >= requiredAbsT) verdict = "BONFERRONI-PASS";
        else if (Math.Abs(t) >= 1.96) verdict = "raw-95-only";
        else verdict = "noise";
        Console.WriteLine($"{kv.Key,-10} {nets.Count,4} {total,8:F3} {mean,8:F4} {t,7:F2} {sr,7:F3} {lo,10:F3} {hi,10:F3} {dsr,8:F3} {verdict}");
    }

    // Aggregate: pooled across symbols (treats every trade as one observation).
    var allNets = bySymbol.SelectMany(kv => kv.Value.Select(v => v.Net)).ToList();
    if (allNets.Count >= 2)
    {
        var t = ThatsMyQuant.Application.Strategy.StatisticalSignificance.TStatisticVsZero(allNets);
        var sr = ThatsMyQuant.Application.Strategy.StatisticalSignificance.SharpePerTrade(allNets);
        var (lo, hi, _) = ThatsMyQuant.Application.Strategy.StatisticalSignificance.BootstrapSharpeCI(allNets, iterations: 5000);
        var dsr = ThatsMyQuant.Application.Strategy.StatisticalSignificance.DeflatedSharpeRatioProbability(allNets, 1, sr);
        Console.WriteLine();
        Console.WriteLine($"=== Pooled portfolio (all {allNets.Count} trades) ===");
        Console.WriteLine($"  Total net: ${allNets.Sum():F3}, Mean: ${allNets.Average():F4}");
        Console.WriteLine($"  t-stat vs 0: {t:F2}, per-trade Sharpe: {sr:F3}");
        Console.WriteLine($"  Bootstrapped 95% Sharpe CI: [{lo:F3}, {hi:F3}]");
        Console.WriteLine($"  DSR probability of true Sharpe > 0: {dsr:F3}");
        if (Math.Abs(t) < 1.96) Console.WriteLine("  [WEAK] Pooled t-stat below 95% — sample too small to claim edge.");
        else if (dsr < 0.5) Console.WriteLine("  [WEAK] DSR < 0.5 — Sharpe likely inflated by noise/multiplicity.");
        else if (dsr >= 0.95) Console.WriteLine("  [STRONG] DSR >= 0.95 — high confidence true Sharpe > 0.");
        else Console.WriteLine($"  [BORDERLINE] DSR = {dsr:F3} — more data needed for a firm verdict.");
    }
}

static async Task RunHarvestFeesAsync(IServiceProvider services, string[] args)
{
    // Pulls real OKX fills for [since, until], joins with closed paper positions by
    // (symbol, exit-time within window), and reports real-vs-assumed fee per trade.
    // Replaces the 28bp `Tca:AssumedRoundTripFeeRate` guess with measured ground truth.
    var sinceArg = args.FirstOrDefault(a => a.StartsWith("--since=", StringComparison.OrdinalIgnoreCase));
    var untilArg = args.FirstOrDefault(a => a.StartsWith("--until=", StringComparison.OrdinalIgnoreCase));
    var portfolioArg = args.FirstOrDefault(a => a.StartsWith("--portfolio=", StringComparison.OrdinalIgnoreCase));
    var outArg = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase));

    DateTime since = sinceArg is null
        ? DateTime.UtcNow.AddDays(-7)
        : DateTime.Parse(sinceArg.Substring("--since=".Length), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    DateTime until = untilArg is null
        ? DateTime.UtcNow
        : DateTime.Parse(untilArg.Substring("--until=".Length), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
    string portfolioPath = portfolioArg is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant", "paper_portfolio.json")
        : portfolioArg.Substring("--portfolio=".Length);
    string outPath = outArg is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThatsMyQuant", $"real_fees_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv")
        : outArg.Substring("--out=".Length);

    Console.WriteLine($"[harvest-fees] window: {since:u} → {until:u}");
    Console.WriteLine($"[harvest-fees] portfolio: {portfolioPath}");

    // Manually wire OkxExecutionService — CLI doesn't register IExchangeService.
    var http = services.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient();
    var cfg = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
    var okxLogger = services.GetRequiredService<ILogger<ThatsMyQuant.Infrastructure.Services.OkxExecutionService>>();
    var portfolioStub = services.GetRequiredService<ThatsMyQuant.Contracts.Interfaces.ISharedPortfolioStateManager>();
    var okx = new ThatsMyQuant.Infrastructure.Services.OkxExecutionService(
        okxLogger, http, cfg, portfolioStub, apiVault: null, operationalTelemetry: null, streamingService: null);

    var apiKey = cfg["OKX:ApiKey"] ?? "";
    var pass = cfg["OKX:Passphrase"] ?? "";
    var secret = cfg["OKX:SecretKey"] ?? "";
    Console.WriteLine($"[harvest-fees] OKX:IsDemoMode = {Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<bool>(cfg, "OKX:IsDemoMode")}");
    Console.WriteLine($"[harvest-fees] OKX:ApiKey       len={apiKey.Length}");
    Console.WriteLine($"[harvest-fees] OKX:Passphrase   len={pass.Length}");
    Console.WriteLine($"[harvest-fees] OKX:SecretKey    len={secret.Length}");
    Console.WriteLine("[harvest-fees] calling OKX /api/v5/trade/fills-history (SWAP)...");
    var fills = await okx.GetFillsHistoryAsync(since, until, "SWAP", CancellationToken.None);
    Console.WriteLine($"[harvest-fees] fetched {fills.Count} fills");

    if (fills.Count == 0)
    {
        Console.WriteLine("[harvest-fees] no fills returned — check API key permissions / time window / IsDemoMode flag.");
        return;
    }

    // Group fills by symbol; each closing fill should pair with an opening fill of opposite side.
    // For first-pass we just dump per-fill rows + per-symbol aggregate.
    var bySymbol = fills
        .GroupBy(f => f.Symbol)
        .OrderBy(g => g.Key)
        .ToList();

    Console.WriteLine();
    Console.WriteLine("=== Per-symbol real-fee summary ===");
    Console.WriteLine($"{"Symbol",-12} {"Fills",5} {"GrossNotional",16} {"TotalFee",10} {"FeeBpsAvg",10}");
    decimal totalNotional = 0m, totalFee = 0m;
    foreach (var g in bySymbol)
    {
        decimal symNotional = 0m, symFee = 0m;
        foreach (var f in g) { symNotional += f.Quantity * f.Price; symFee += f.FeePaid; }
        var bps = symNotional > 0 ? (symFee / symNotional) * 10_000m : 0m;
        Console.WriteLine($"{g.Key,-12} {g.Count(),5} {symNotional,16:N2} {symFee,10:N4} {bps,10:N2}");
        totalNotional += symNotional;
        totalFee += symFee;
    }
    var totalBps = totalNotional > 0 ? (totalFee / totalNotional) * 10_000m : 0m;
    Console.WriteLine(new string('-', 65));
    Console.WriteLine($"{"TOTAL",-12} {fills.Count,5} {totalNotional,16:N2} {totalFee,10:N4} {totalBps,10:N2} bps per LEG");
    Console.WriteLine($"     → implied round-trip fee = {totalBps * 2m,10:N2} bps");
    Console.WriteLine($"     vs Tca:AssumedRoundTripFeeRate = 28.00 bps");
    Console.WriteLine();

    // Try to match against paper portfolio closed trades.
    if (System.IO.File.Exists(portfolioPath))
    {
        try
        {
            var json = System.IO.File.ReadAllText(portfolioPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ClosedPositions", out var closed))
            {
                Console.WriteLine("=== Per-trade match (closed paper positions × OKX fills) ===");
                Console.WriteLine($"{"Symbol",-10} {"Side",4} {"EntryPx",10} {"ExitPx",10} {"Qty",10} {"PnL",9} {"AssumedFee",11} {"RealFeeMatched",15} {"Match"}");

                using var sw = new StreamWriter(outPath);
                sw.WriteLine("Symbol,Side,EntryTime,ExitTime,EntryPx,ExitPx,Qty,RealizedPnL,AssumedFee28bp,MatchedFills,RealFeeSum,DeltaFee");

                int matched = 0, unmatched = 0;
                decimal sumAssumed = 0m, sumReal = 0m;
                foreach (var t in closed.EnumerateArray())
                {
                    var sym = t.GetProperty("Symbol").GetString() ?? "";
                    var side = t.GetProperty("Side").GetInt32();
                    var entryPx = t.GetProperty("EntryPrice").GetDecimal();
                    var exitPx = t.GetProperty("CurrentPrice").GetDecimal();
                    var qty = t.GetProperty("Quantity").GetDecimal();
                    var pnl = t.GetProperty("RealizedPnL").GetDecimal();
                    var entryT = DateTime.Parse(t.GetProperty("EntryTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
                    var exitT = DateTime.Parse(t.GetProperty("ExitTime").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

                    var notionalEntry = entryPx * qty;
                    var notionalExit = exitPx * qty;
                    var assumedFee = (notionalEntry + notionalExit) * 0.0014m; // 14bp per leg ≈ 28bp round-trip

                    // Match fills: same symbol, time within ±5 min of entry OR exit
                    var matches = fills.Where(f => f.Symbol == sym &&
                        ((f.FillTimeUtc >= entryT.AddMinutes(-5) && f.FillTimeUtc <= entryT.AddMinutes(5)) ||
                         (f.FillTimeUtc >= exitT.AddMinutes(-5)  && f.FillTimeUtc <= exitT.AddMinutes(5)))).ToList();
                    var realFee = matches.Sum(m => m.FeePaid);

                    if (matches.Count > 0) { matched++; sumReal += realFee; sumAssumed += assumedFee; }
                    else unmatched++;

                    Console.WriteLine($"{sym,-10} {(side==0?"LONG":"SHORT"),4} {entryPx,10:F4} {exitPx,10:F4} {qty,10:F2} {pnl,9:F3} {assumedFee,11:F4} {realFee,15:F4} {matches.Count}");
                    sw.WriteLine($"{sym},{(side==0?"LONG":"SHORT")},{entryT:o},{exitT:o},{entryPx},{exitPx},{qty},{pnl},{assumedFee:F6},{matches.Count},{realFee:F6},{(realFee-assumedFee):F6}");
                }
                Console.WriteLine();
                Console.WriteLine($"=== Summary ({matched} matched, {unmatched} unmatched) ===");
                Console.WriteLine($"  Sum assumed fee (28bp model): {sumAssumed:F4}");
                Console.WriteLine($"  Sum real fee (OKX fills):     {sumReal:F4}");
                if (sumAssumed > 0)
                {
                    var ratio = sumReal / sumAssumed;
                    Console.WriteLine($"  Ratio real/assumed:           {ratio:F3}x");
                    Console.WriteLine($"  → calibrated round-trip rate: {28m * ratio:F2} bps (vs current 28bp assumption)");
                }
                Console.WriteLine();
                Console.WriteLine($"CSV written: {outPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[harvest-fees] portfolio match failed: {ex.Message}");
        }
    }
    else
    {
        Console.WriteLine($"[harvest-fees] portfolio file not found: {portfolioPath} (skipping per-trade match)");
    }
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
