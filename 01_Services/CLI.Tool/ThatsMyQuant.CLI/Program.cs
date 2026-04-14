using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Common;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddThatsMyQuantApplication();
builder.Services.AddThatsMyQuantInfrastructure(builder.Configuration);
builder.Logging.AddConsole();

var host = builder.Build();

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

switch (command)
{
    case "backtest":
        await RunBacktestAsync(host.Services, args);
        break;

    case "walkforward":
        await RunWalkForwardAsync(host.Services, args);
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

    var symbol = GetArg(args, "--symbol", "BTC-USDT");
    var timeframe = GetArg(args, "--timeframe", "1h");
    var capital = decimal.Parse(GetArg(args, "--capital", "100000"));
    var daysBack = int.Parse(GetArg(args, "--days", "90"));

    var end = DateTime.UtcNow;
    var start = end.AddDays(-daysBack);

    Console.WriteLine($"=== ThatsMyQuant Backtest CLI ===");
    Console.WriteLine($"Symbol:    {symbol}");
    Console.WriteLine($"Timeframe: {timeframe}");
    Console.WriteLine($"Period:    {start:yyyy-MM-dd} -> {end:yyyy-MM-dd} ({daysBack} days)");
    Console.WriteLine($"Capital:   {capital:N0}");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    await orchestrator.RunAutomatedSessionAsync(symbol, timeframe, start, end, capital);
    sw.Stop();

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
    var result = await engine.RunAsync(candles, symbol, windows, trainRatio, capital);
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
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --symbol <sym>        Trading pair (default: BTC-USDT)");
    Console.WriteLine("  --timeframe <tf>      Candle interval: 1m,5m,15m,1h,4h,1d (default: 1h)");
    Console.WriteLine("  --capital <amount>    Starting capital (default: 100000)");
    Console.WriteLine("  --days <n>            Historical days to fetch (default: 90/180)");
    Console.WriteLine("  --windows <n>         Walk-forward windows (default: 6)");
    Console.WriteLine("  --train-ratio <r>     Train/test split ratio (default: 0.7)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run -- backtest --symbol ETH-USDT --timeframe 4h --days 120");
    Console.WriteLine("  dotnet run -- walkforward --symbol BTC-USDT --windows 8 --days 365");
}
