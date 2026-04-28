using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodically replays the validated pairs-trading strategy for each configured pair against
/// the latest OKX candles, mirroring any newly-closed trades into the shared PaperPortfolio.
///
/// Runs alongside the directional PaperTradingHostedService (which is disabled by default).
/// State is per-pair `_lastSeenExitPerPair` — initialised at startup to "now" so historical
/// trades from the first backtest pass aren't replayed; only trades that close after startup
/// flow into the portfolio.
/// </summary>
public sealed class PairsTradingPaperHostedService : BackgroundService
{
    private readonly ILogger<PairsTradingPaperHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly PaperPortfolio _portfolio;
    private readonly IOperatorAlertPublisher? _alerts;
    private readonly TimeSpan _interval;
    private readonly string _timeframe;
    private readonly int _candleLookback;
    private readonly bool _enabled;
    private readonly List<PairConfig> _pairs;
    private readonly Dictionary<string, DateTime> _lastSeenExitPerPair = new();

    public PairsTradingPaperHostedService(
        ILogger<PairsTradingPaperHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IOperatorAlertPublisher? alerts = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _portfolio = portfolio;
        _alerts = alerts;

        _enabled = bool.TryParse(configuration["Strategy:PairsTrading:Enabled"], out var en) ? en : true;
        // Suppress legacy mirror when PairsHybridExecutor is in dry-run/live mode (avoids double-record).
        var hybridMode = (configuration["Strategy:PairsTrading:HybridMode"] ?? "off").Trim().ToLowerInvariant();
        if (hybridMode == "dry-run" || hybridMode == "dryrun" || hybridMode == "live")
        {
            _enabled = false;
            logger.LogInformation("PairsTradingPaperHostedService disabled because Strategy:PairsTrading:HybridMode={Mode} (PairsHybridExecutor takes over).", hybridMode);
        }
        _timeframe = configuration["Strategy:PairsTrading:Timeframe"] ?? "4h";
        _candleLookback = int.TryParse(configuration["Strategy:PairsTrading:CandleLookback"], out var cl) && cl > 0 ? cl : 300;

        var intervalSeconds = int.TryParse(configuration["Strategy:PairsTrading:IntervalSeconds"], out var iv) && iv > 0 ? iv : 300;
        _interval = TimeSpan.FromSeconds(intervalSeconds);

        _pairs = new List<PairConfig>();
        // CSV format: "SymbolY:SymbolX:WeightFraction,SymbolY:SymbolX:WeightFraction"
        // WeightFraction is a fraction of PaperPortfolio.InitialBalance (e.g. 0.50 = 50% allocation).
        // Backwards-compat: if value > 1.0 we treat it as a legacy absolute USD amount and convert
        // to a fraction by dividing by 100,000 (the historical paper baseline).
        var csvFallback = configuration["Strategy:PairsTrading:AllowedPairsCsv"];
        if (!string.IsNullOrWhiteSpace(csvFallback))
        {
            foreach (var entry in csvFallback.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    decimal weight = 0.10m;
                    if (parts.Length >= 3 && decimal.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var raw))
                    {
                        weight = raw > 1.0m ? raw / 100_000m : raw;
                    }
                    _pairs.Add(new PairConfig(parts[0], parts[1], weight));
                }
            }
            if (_pairs.Count > 0)
                logger.LogInformation("PairsTradingPaperHostedService loaded {N} pairs from CSV.", _pairs.Count);
        }
        // Validated Kelly-weighted portfolio (memory: portfolio_backtest_sharpe387 + pro_portfolio_breadth).
        // Weights as fractions of InitialBalance. The fallback fires ONLY when no config CSV was
        // supplied — this happens when appsettings.Paper.json is misloaded (which has historically
        // been a real bug). Loud warning so it can't be silently missed.
        if (_pairs.Count == 0)
        {
            logger.LogError(
                "PairsTradingPaperHostedService: NO CONFIG SUPPLIED for Strategy:PairsTrading:AllowedPairsCsv. " +
                "Loading hardcoded validated Kelly portfolio (TRX/XLM, SOL/AVAX, SOL/LINK). " +
                "Verify appsettings.Paper.json is being loaded — see [DIAG] line in Program.cs.");
            _pairs.Add(new PairConfig("TRXUSDT", "XLMUSDT", WeightFraction: 0.50m));   // PF 4.89 — dominant
            _pairs.Add(new PairConfig("SOLUSDT", "AVAXUSDT", WeightFraction: 0.15m));  // PF 2.87 OOS
            _pairs.Add(new PairConfig("SOLUSDT", "LINKUSDT", WeightFraction: 0.10m));  // PF 1.23-1.63
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || _pairs.Count == 0)
        {
            _logger.LogInformation("PairsTradingPaperHostedService disabled (Enabled={Enabled}, Pairs={Count}).", _enabled, _pairs.Count);
            return;
        }

        // Initialise state to "now" so the first tick's historical trade replay doesn't double-book.
        var startupCutoff = DateTime.UtcNow;
        foreach (var p in _pairs) _lastSeenExitPerPair[p.Key] = startupCutoff;

        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning(
                "PairsTradingPaperHostedService: PaperPortfolio.InitialBalance=0 at startup — waiting for ExchangeBalanceSyncHostedService to seed real OKX balance. Will start ticking when basis>0.");
        }
        _logger.LogInformation(
            "PairsTradingPaperHostedService starting. Pairs={Pairs} Timeframe={Tf} Interval={Interval}s InitialBasis=${Basis:N2} TotalWeight={Weight:P1}",
            string.Join(",", _pairs.Select(p => $"{p.Key}={p.WeightFraction:P1}")),
            _timeframe, _interval.TotalSeconds, basis, _pairs.Sum(p => p.WeightFraction));

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try
                {
                    await ProcessAllPairsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PairsTradingPaperHostedService tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAllPairsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        foreach (var pair in _pairs)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                await ProcessPairAsync(pair, marketData, loggerFactory, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pair tick failed for {Pair}", pair.Key);
            }
        }

        _logger.LogInformation(
            "Pairs paper portfolio: Cash=${Cash:N2} Equity=${Equity:N2} Closed={Closed} (paper-aggregate)",
            _portfolio.Cash, _portfolio.TotalEquity, _portfolio.ClosedPositions.Count);
    }

    private async Task ProcessPairAsync(
        PairConfig pair,
        IMarketDataService marketData,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var candlesY = await marketData.GetCandlesAsync(pair.SymbolY, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
        var candlesX = await marketData.GetCandlesAsync(pair.SymbolX, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);

        if (candlesY == null || candlesX == null || candlesY.Count < 100 || candlesX.Count < 100)
        {
            _logger.LogDebug("Pair {Pair} insufficient candles (Y={Y}, X={X})", pair.Key, candlesY?.Count ?? 0, candlesX?.Count ?? 0);
            return;
        }

        // [2026-04-27 evening wiring] Read from config so paper restart reflects all
        // backtest-driven tuning without code change. Defaults match historical behavior;
        // overrides come from appsettings.Paper.json.
        var pairLev = ParseDecimal(_configuration["Strategy:PairsTrading:Leverage"], 7.0m);
        var pairMaxLev = ParseDecimal(_configuration["Strategy:PairsTrading:MaxLeverage"], 10.0m);
        var pairQualityMin = int.TryParse(_configuration["Strategy:PairsTrading:MinPairQualityScore"], out var pqs) ? pqs : 0;
        var entryZ = double.TryParse(_configuration["Strategy:PairsTrading:ZScoreEntry"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ez) ? ez : 2.0;
        var exitZ = double.TryParse(_configuration["Strategy:PairsTrading:ZScoreExit"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var xz) ? xz : 0.3;
        var stopZ = double.TryParse(_configuration["Strategy:PairsTrading:ZScoreStop"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sz) ? sz : 3.0;
        var maxHurst = double.TryParse(_configuration["Strategy:PairsTrading:MaxHurstExponent"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var mhe) ? mhe : 0.55;
        var maxHL = double.TryParse(_configuration["Strategy:PairsTrading:MaxHalfLifeBars"],
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var mhl) ? mhl : 50.0;

        // Per-pair options instance so SymbolA/B labels are correct for logging + Reasoning strings.
        var pairOptions = new PairsTradingOptions
        {
            SymbolA = pair.SymbolY,
            SymbolB = pair.SymbolX,
            UseKalmanHedgeRatio = true,
            KalmanProcessVariance = 1e-4,
            KalmanInitialVariance = 1.0,
            CointegrationLookbackBars = 180,
            ZScoreWindow = 30,
            MinCorrelation = 0.30,
            MaxHedgeRatio = 15.00m,
            MinHedgeRatio = 0.10m,
            // Wired from appsettings.Paper.json — ZScoreEntry/Exit/Stop, MaxHurst, MaxHalfLife.
            EntryZScoreThreshold = entryZ,
            ExitZScoreThreshold = exitZ,
            StopLossZScoreThreshold = stopZ,
            MaxHurstExponent = maxHurst,
            MaxHalfLifeBars = maxHL,
            Leverage = pairLev,
            MaxLeverage = pairMaxLev,
            MinPairQualityScore = pairQualityMin,
        };
        var runner = new PairsBacktestRunner(loggerFactory.CreateLogger<PairsBacktestRunner>(), Options.Create(pairOptions));

        // Capital sized off InitialBalance (synced from OKX demo balance via ExchangeBalanceSyncHostedService).
        // Refuse to size positions when basis is zero — that signals a sync failure, and faking $100k
        // would mislead PnL/Sharpe/Kelly downstream.
        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning("Pair {Pair} skipped: PaperPortfolio.InitialBalance=0 (waiting for OKX balance sync).", pair.Key);
            return;
        }
        var capital = basis * pair.WeightFraction;
        var result = runner.Run(candlesY, candlesX, capital);

        var lastSeen = _lastSeenExitPerPair[pair.Key];
        var newTrades = result.Trades.Where(t => t.ExitTime > lastSeen).OrderBy(t => t.ExitTime).ToList();
        if (newTrades.Count == 0) return;

        foreach (var trade in newTrades)
        {
            MirrorTradeToPortfolio(pair, trade);
            _lastSeenExitPerPair[pair.Key] = trade.ExitTime;
        }

        var totalPnl = newTrades.Sum(t => t.PnL);
        _logger.LogInformation(
            "Pair {Pair} mirrored {N} new closed trade(s); cumulative pair PnL contribution this tick=${PnL:N2}",
            pair.Key, newTrades.Count, totalPnl);

        if (_alerts is not null)
        {
            var sev = totalPnl >= 0 ? "success" : "warning";
            var icon = totalPnl >= 0 ? "🟢" : "🔴";
            _ = _alerts.PublishAsync(
                sev,
                "Pairs",
                $"{icon} {pair.Key} closed {newTrades.Count} trade(s)",
                $"Net ${totalPnl:N2} · last reason: {newTrades[^1].ExitReason}",
                CancellationToken.None);
        }
    }

    private void MirrorTradeToPortfolio(PairConfig pair, BacktestTradeResult trade)
    {
        // The runner returns spread-level trades (Side.Buy = LongSpread, Side.Sell = ShortSpread).
        // Mirror as a single PaperPosition tagged "PairsTrading" using the pair key as the symbol.
        // Cash already reflects realised PnL via Portfolio.Cash += trade.PnL — we don't tie up
        // notional like a directional position because the runner's equity curve is the source
        // of truth for sizing.
        var position = new PaperPosition
        {
            Symbol = pair.Key,
            Side = trade.Side,
            EntryPrice = trade.EntryPrice,
            CurrentPrice = trade.ExitPrice,
            Quantity = 0m,                  // notional is internal to runner; no real lot size
            EntryTime = trade.EntryTime,
            ExitTime = trade.ExitTime,
            RealizedPnL = trade.PnL,
            ExitReason = trade.ExitReason,
            Reasoning = trade.Reasoning,
            Confidence = trade.Confidence,
            StrategyTag = "PairsTrading",
            RegimeTag = trade.RegimeTag,
            ExecutionMode = "PAPER",
            PromotionStage = "Paper"
        };

        lock (_portfolio)
        {
            _portfolio.ClosedPositions.Add(position);
            _portfolio.Cash += trade.PnL;
            _portfolio.DailyPnL += trade.PnL;
            _portfolio.EquityCurve.Add(_portfolio.TotalEquity);
        }

        _logger.LogInformation(
            "PAIRS TRADE [{Pair}] {Side} entryAt={EntryTs:u} exitAt={ExitTs:u} PnL=${PnL:N2} reason={Reason}",
            pair.Key, trade.Side, trade.EntryTime, trade.ExitTime, trade.PnL, trade.ExitReason);
    }

    private sealed record PairConfig(string SymbolY, string SymbolX, decimal WeightFraction = 0.10m)
    {
        public string Key => $"{SymbolY}-{SymbolX}";
    }

    private static decimal ParseDecimal(string? raw, decimal fallback)
        => decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
