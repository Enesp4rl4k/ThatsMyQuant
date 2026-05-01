using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;

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
    private readonly bool _syncWithExchange;
    // Default OFF: the legacy directional scanner uses tight Range/MTF gates that reject every
    // signal on crypto 1h/4h. Real validated alpha lives in PairsTradingPaperHostedService and
    // FundingAlphaPaperHostedService. Flip true only for directional research.
    private readonly bool _directionalScannerEnabled;

    public PaperTradingHostedService(
        ILogger<PaperTradingHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        var symbolsCsv = configuration["Paper:Symbols"]
            ?? configuration["TMQ_PAPER_SYMBOLS"]
            ?? "SOL-USDT,AVAX-USDT,DOGE-USDT";
        _symbols = symbolsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var intervalSeconds = int.TryParse(
            configuration["Paper:IntervalSeconds"] ?? configuration["TMQ_PAPER_INTERVAL_SECONDS"],
            out var parsed) && parsed > 0 ? parsed : 60;
        _interval = TimeSpan.FromSeconds(intervalSeconds);

        // Opt-in: seed paper Portfolio.Cash / InitialBalance from the real OKX account
        // balance at startup. Default off to preserve deterministic $100k backtests;
        // flip on for realistic paper runs that mirror live capital.
        _syncWithExchange = bool.TryParse(configuration["Paper:SyncWithExchange"], out var syncFlag) && syncFlag;

        _directionalScannerEnabled = bool.TryParse(configuration["Paper:DirectionalScannerEnabled"], out var dirFlag) && dirFlag;
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
            "PaperTradingHostedService starting. DirectionalScannerEnabled={Enabled} Symbols={Symbols}, Interval={Interval}s",
            _directionalScannerEnabled, string.Join(",", _symbols), _interval.TotalSeconds);

        // Single long-lived scope so PaperTradingService (and its Portfolio) survive across ticks.
        var paperService = scope.ServiceProvider.GetRequiredService<PaperTradingService>();
        var persistence = scope.ServiceProvider.GetService<PaperPortfolioPersistenceService>();

        // Restore-on-startup before any exchange sync — if a prior run's snapshot exists,
        // hydrate it. SyncWithExchange below may then overwrite InitialBalance if configured.
        if (persistence is not null)
        {
            var snapshot = persistence.TryLoad();
            if (snapshot is not null)
            {
                PaperPortfolioPersistenceService.ApplySnapshot(paperService.Portfolio, snapshot);
            }
        }

        if (_syncWithExchange)
        {
            await TrySyncInitialBalanceFromExchangeAsync(scope.ServiceProvider, paperService, stoppingToken);
        }

        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_directionalScannerEnabled)
                {
                    try
                    {
                        await ProcessAllSymbolsAsync(paperService, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "PaperTradingHostedService tick failed.");
                    }
                }
                else
                {
                    // Heartbeat-only mode: portfolio snapshot for dashboard visibility while
                    // the real edges run in PairsTradingPaperHostedService / FundingAlphaPaperHostedService.
                    var portfolio = paperService.Portfolio;
                    _logger.LogInformation(
                        "Paper portfolio: Cash=${Cash:N2} Equity=${Equity:N2} Open={Open} Closed={Closed} DailyPnL=${DailyPnL:N2}",
                        portfolio.Cash, portfolio.TotalEquity, portfolio.OpenPositions.Count, portfolio.ClosedPositions.Count, portfolio.DailyPnL);
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
        finally
        {
            // Graceful shutdown: bypass the debounce and persist the final state so
            // a restart picks up exactly where we left off.
            if (persistence is not null)
            {
                try
                {
                    await persistence.FlushAsync(paperService.Portfolio, CancellationToken.None);
                    _logger.LogInformation("Paper portfolio flushed to {Path} on shutdown.", persistence.FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Paper portfolio flush on shutdown failed.");
                }
            }
        }
    }

    private async Task TrySyncInitialBalanceFromExchangeAsync(
        IServiceProvider scope,
        PaperTradingService paperService,
        CancellationToken ct)
    {
        var exchange = scope.GetService<IExchangeService>();
        if (exchange is null)
        {
            _logger.LogWarning("Paper:SyncWithExchange=true but no IExchangeService registered — keeping default balance.");
            return;
        }

        try
        {
            var account = await exchange.GetAccountStatusAsync().WaitAsync(ct).ConfigureAwait(false);
            if (account is null || account.Balance <= 0m)
            {
                _logger.LogWarning(
                    "Paper:SyncWithExchange — exchange returned zero/null balance; keeping default ${Cash:N2}.",
                    paperService.Portfolio.Cash);
                return;
            }

            var previousCash = paperService.Portfolio.Cash;
            paperService.Portfolio.InitialBalance = account.Balance;
            paperService.Portfolio.Cash = account.Balance;
            paperService.Portfolio.EquityCurve.Clear();
            paperService.Portfolio.AppendEquity(paperService.Portfolio.TotalEquity);
            _logger.LogWarning(
                "Paper:SyncWithExchange — seeded from real {Provider} account {Id}: ${Balance:N2} (was ${Prev:N2}).",
                account.Provider, account.Id, account.Balance, previousCash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Paper:SyncWithExchange failed — keeping default balance ${Cash:N2}. Check OKX:ApiKey/Passphrase.",
                paperService.Portfolio.Cash);
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
            "Paper portfolio: Cash=${Cash:N2} Equity=${Equity:N2} Open={Open} Closed={Closed} DailyPnL=${DailyPnL:N2}",
            portfolio.Cash,
            portfolio.TotalEquity,
            portfolio.OpenPositions.Count,
            portfolio.ClosedPositions.Count,
            portfolio.DailyPnL);
    }
}
