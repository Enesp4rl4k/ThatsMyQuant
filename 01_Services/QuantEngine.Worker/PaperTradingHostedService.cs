using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodically runs PaperTradingService across a configured symbol watchlist.
/// Activates only when TMQ_MODE=paper. In live mode, signals flow via TickPipeline/MediatR instead.
/// </summary>
public sealed class PaperTradingHostedService : BackgroundService
{
    private readonly ILogger<PaperTradingHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private string[] _symbols;
    private readonly TimeSpan _interval;

    public PaperTradingHostedService(
        ILogger<PaperTradingHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        var symbolsCsv = configuration["Paper:Symbols"]
            ?? configuration["TMQ_PAPER_SYMBOLS"]
            ?? "BTC-USDT,ETH-USDT,SOL-USDT";
        _symbols = symbolsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var intervalSeconds = int.TryParse(
            configuration["Paper:IntervalSeconds"] ?? configuration["TMQ_PAPER_INTERVAL_SECONDS"],
            out var parsed) && parsed > 0 ? parsed : 60;
        _interval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();

        if (_symbols.Length == 0 || _symbols[0].Equals("DYNAMIC", StringComparison.OrdinalIgnoreCase))
        {
            var dynService = scope.ServiceProvider.GetRequiredService<ThatsMyQuant.Application.Strategy.DynamicSymbolUniverseService>();
            var dynRes = await dynService.BuildUniverseAsync("Crypto", maxSymbols: 5, cancellationToken: stoppingToken);
            _symbols = dynRes.SelectedSymbols.Select(s => s.Symbol).ToArray();
            _logger.LogInformation("PaperTrading dynamic universe resolved to: {Symbols}", string.Join(',', _symbols));
        }

        _logger.LogInformation(
            "PaperTradingHostedService starting. Symbols={Symbols}, Interval={Interval}s",
            string.Join(",", _symbols), _interval.TotalSeconds);

        // Single long-lived scope so PaperTradingService (and its Portfolio) survive across ticks.
        var paperService = scope.ServiceProvider.GetRequiredService<PaperTradingService>();

        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAllSymbolsAsync(paperService, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PaperTradingHostedService tick failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessAllSymbolsAsync(PaperTradingService paperService, CancellationToken stoppingToken)
    {
        foreach (var symbol in _symbols)
        {
            if (stoppingToken.IsCancellationRequested) return;

            try
            {
                var result = await paperService.ProcessSymbolAsync(symbol);
                _logger.LogInformation("PAPER [{Symbol}] {Result}", symbol, result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Paper trading failed for {Symbol}", symbol);
            }
        }

        var portfolio = paperService.Portfolio;
        _logger.LogInformation(
            "Paper portfolio: Cash=₺{Cash:N2} Equity=₺{Equity:N2} Open={Open} Closed={Closed} DailyPnL=₺{DailyPnL:N2}",
            portfolio.Cash,
            portfolio.TotalEquity,
            portfolio.OpenPositions.Count,
            portfolio.ClosedPositions.Count,
            portfolio.DailyPnL);
    }
}
