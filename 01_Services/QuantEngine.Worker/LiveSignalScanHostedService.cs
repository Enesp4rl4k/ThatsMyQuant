using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodically pulls OHLC for configured symbols and runs the full <see cref="ProcessSignalCommand"/> pipeline
/// (technical analysis, swarm, optional edge, execution handoff) so QuantEngine performs quantitative analysis
/// without a second WebSocket — tick/HFT flow remains in Execution.Worker.
/// </summary>
public sealed class LiveSignalScanHostedService : BackgroundService
{
    private readonly ILogger<LiveSignalScanHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _interval;
    private string[] _symbols;
    private readonly string _exchangeLabel;

    public LiveSignalScanHostedService(
        ILogger<LiveSignalScanHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        var minutes = int.TryParse(configuration["QuantEngine:LiveSignalScan:IntervalMinutes"], out var m) ? Math.Clamp(m, 1, 240) : 15;
        _interval = TimeSpan.FromMinutes(minutes);
        var raw = configuration["QuantEngine:LiveSignalScan:Symbols"] ?? string.Empty;
        _symbols = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _exchangeLabel = configuration["TMQ_EXCHANGE_MODE"] ?? configuration["ExchangeMode"] ?? "Binance";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_symbols.Length == 0)
        {
            _logger.LogWarning("LiveSignalScanHostedService: no symbols configured; exiting.");
            return;
        }

        _logger.LogInformation(
            "LiveSignalScanHostedService started. Configured Symbols={Symbols}, Interval={Interval}, Exchange={Exchange}",
            string.Join(',', _symbols),
            _interval,
            _exchangeLabel);

        using var scope = _serviceProvider.CreateScope();
        if (_symbols.Length == 0 || _symbols[0].Equals("DYNAMIC", StringComparison.OrdinalIgnoreCase))
        {
            var dynService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.DynamicSymbolUniverseService>();
            var marketLabel = _exchangeLabel.Equals("Binance", StringComparison.OrdinalIgnoreCase) || _exchangeLabel.Equals("OKX", StringComparison.OrdinalIgnoreCase) ? "Crypto" : _exchangeLabel;
            var dynRes = await dynService.BuildUniverseAsync(marketLabel, maxSymbols: 5, cancellationToken: stoppingToken);
            _symbols = dynRes.SelectedSymbols.Select(s => s.Symbol).ToArray();
            _logger.LogInformation("Dynamic universe resolved to: {Symbols}", string.Join(',', _symbols));
        }

        if (_symbols.Length == 0)
        {
            _logger.LogWarning("LiveSignalScanHostedService: final symbol list is empty; exiting.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var symbol in _symbols)
            {
                try
                {
                    await ScanSymbolAsync(symbol, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Live signal scan failed for {Symbol}", symbol);
                }
            }
        }
    }

    private async Task ScanSymbolAsync(string symbol, CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();

        var market = ResolveMarketType(symbol);
        var candles = await marketData.GetCandlesAsync(symbol, 100, market);
        if (candles.Count == 0)
        {
            _logger.LogDebug("Live signal scan: no candles for {Symbol}", symbol);
            return;
        }

        var last = candles[^1];
        var signal = new Signal
        {
            Source = "QuantEngine-LiveScan",
            Symbol = symbol,
            Price = last.Close,
            Timestamp = last.Timestamp,
            Volume24h = 0m,
            Exchange = _exchangeLabel,
            MarketType = market == "Crypto" ? MarketType.Crypto : market == "Forex" ? MarketType.Forex : MarketType.BIST,
            Reasoning = "Scheduled candle scan (QuantEngine)."
        };

        _logger.LogInformation("Live signal scan dispatching ProcessSignal for {Symbol} @ {Price}", symbol, signal.Price);
        await mediator.Send(new ProcessSignalCommand(signal), stoppingToken);
    }

    private static string ResolveMarketType(string symbol)
    {
        if (symbol.Contains('-', StringComparison.OrdinalIgnoreCase))
            return "Crypto";
        return symbol.Contains('=', StringComparison.OrdinalIgnoreCase) ? "Forex" : "BIST";
    }
}
