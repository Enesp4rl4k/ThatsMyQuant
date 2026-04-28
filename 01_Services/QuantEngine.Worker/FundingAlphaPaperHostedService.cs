using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Strategy;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Periodically replays the validated funding-alpha strategy for each configured symbol against
/// the latest OKX 1h candles + funding rates, mirroring any newly-closed trades into the shared
/// PaperPortfolio.
/// </summary>
public sealed class FundingAlphaPaperHostedService : BackgroundService
{
    private readonly ILogger<FundingAlphaPaperHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly PaperPortfolio _portfolio;
    private readonly TimeSpan _interval;
    private readonly string _timeframe;
    private readonly int _candleLookback;
    private readonly bool _enabled;
    // Per-symbol Kelly-weighted capital map (memory: pro_portfolio_breadth — LINK Sharpe 4.95
    // dropped from combined Kelly portfolio due to IS→portfolio flip but still the strongest
    // standalone funding edge; including it here for diversification).
    private readonly Dictionary<string, decimal> _capitalPerSymbol;
    private readonly FundingAlphaConfig _config;
    private readonly Dictionary<string, DateTime> _lastSeenExitPerSymbol = new();

    private readonly IOperatorAlertPublisher? _alerts;

    public FundingAlphaPaperHostedService(
        ILogger<FundingAlphaPaperHostedService> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        PaperPortfolio portfolio,
        IOperatorAlertPublisher? alerts = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _portfolio = portfolio;
        _alerts = alerts;

        _enabled = bool.TryParse(configuration["Funding:Enabled"], out var en) ? en : true;
        // When FundingHybridExecutor is in dry-run or live mode, the legacy mirror MUST be off
        // to prevent double-recording (hybrid handles trade lifecycle directly).
        var hybridMode = (configuration["Funding:HybridMode"] ?? "off").Trim().ToLowerInvariant();
        if (hybridMode == "dry-run" || hybridMode == "dryrun" || hybridMode == "live")
        {
            _enabled = false;
            logger.LogInformation("FundingAlphaPaperHostedService disabled because Funding:HybridMode={Mode} (FundingHybridExecutor takes over).", hybridMode);
        }
        _timeframe = configuration["Funding:Timeframe"] ?? "1h";
        _candleLookback = int.TryParse(configuration["Funding:CandleLookback"], out var cl) && cl > 0 ? cl : 300;

        var intervalSeconds = int.TryParse(configuration["Funding:IntervalSeconds"], out var iv) && iv > 0 ? iv : 1800;
        _interval = TimeSpan.FromSeconds(intervalSeconds);

        // Kelly-weighted per-symbol allocation as fractions of PaperPortfolio.InitialBalance.
        // CSV format: "SYMBOL:WEIGHT,SYMBOL:WEIGHT" (e.g. LINKUSDT:0.08).
        // Backwards-compat: values >1 are treated as legacy absolute USD and divided by 100,000.
        _capitalPerSymbol = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var allocCsv = configuration["Funding:AllocationCsv"];
        if (!string.IsNullOrWhiteSpace(allocCsv))
        {
            foreach (var entry in allocCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && decimal.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var raw))
                {
                    decimal weight = raw > 1.0m ? raw / 100_000m : raw;
                    _capitalPerSymbol[parts[0]] = weight;
                }
            }
            if (_capitalPerSymbol.Count > 0)
                logger.LogInformation("FundingAlphaPaperHostedService loaded {N} symbols from AllocationCsv.", _capitalPerSymbol.Count);
        }
        // Validated portfolio defaults (memory: pro_portfolio_breadth_2026_04_24).
        // Fallback fires ONLY when no config CSV — historically a Paper.json load bug. Loud error.
        if (_capitalPerSymbol.Count == 0)
        {
            logger.LogError(
                "FundingAlphaPaperHostedService: NO CONFIG SUPPLIED for Funding:AllocationCsv. " +
                "Loading hardcoded validated portfolio (LINK/LTC/DOGE/AAVE). " +
                "Verify appsettings.Paper.json is being loaded.");
            _capitalPerSymbol["LINKUSDT"] = 0.08m;   // Sharpe 4.95 — biggest funding edge
            _capitalPerSymbol["LTCUSDT"]  = 0.07m;   // Sharpe 1.68
            _capitalPerSymbol["DOGEUSDT"] = 0.05m;   // Sharpe 1.24
            _capitalPerSymbol["AAVEUSDT"] = 0.05m;   // Sharpe 0.99
        }

        // Funding-strategy thresholds — defaults match FundingAlphaConfig record defaults.
        // [2026-04-27] Per-strategy leverage: ablation showed funding edge dies above
        // 3-4x leverage because the DD throttle (60% of 5% budget) trips on small
        // funding-leg variance and halves the leverage. Keep funding at 3x while pairs
        // run at 7x. Configurable via Funding:Leverage.
        _config = new FundingAlphaConfig(
            EntryThreshold: ParseDecimal(configuration["Funding:EntryThresholdRate"], 0.0003m),
            ExitThreshold: ParseDecimal(configuration["Funding:ExitThresholdRate"], 0.00005m),
            TakeProfitPct: ParseDecimal(configuration["Funding:TakeProfitPct"], 0.02m),
            StopLossPct: ParseDecimal(configuration["Funding:StopLossPct"], 0.015m),
            MaxHoldingBars: int.TryParse(configuration["Funding:MaxHoldingBars"], out var mh) && mh > 0 ? mh : 16,
            BaseSizeFraction: ParseDecimal(configuration["Funding:BaseSizeFraction"], 0.05m),
            MaxSizeFraction: ParseDecimal(configuration["Funding:MaxSizeFraction"], 0.15m),
            StrengthScale: 1.0,
            MaxStrengthMult: 3.0m,
            RoundTripCostRate: 0.0008m,
            Leverage: ParseDecimal(configuration["Funding:Leverage"], 3.0m),
            MaxLeverage: ParseDecimal(configuration["Funding:MaxLeverage"], 5.0m),
            DrawdownThrottleFraction: 0.60m,
            MaxDrawdownBudget: 0.05m,
            UseFlipCatalyst: bool.TryParse(configuration["Funding:UseFlipCatalyst"], out var ufc) && ufc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || _capitalPerSymbol.Count == 0)
        {
            _logger.LogInformation("FundingAlphaPaperHostedService disabled (Enabled={Enabled}, Universe={Count}).", _enabled, _capitalPerSymbol.Count);
            return;
        }

        var startupCutoff = DateTime.UtcNow;
        foreach (var s in _capitalPerSymbol.Keys) _lastSeenExitPerSymbol[s] = startupCutoff;

        var basis = _portfolio.InitialBalance;
        _logger.LogInformation(
            "FundingAlphaPaperHostedService starting. Allocations={Allocations} Timeframe={Tf} Interval={Interval}s Basis=${Basis:N2} TotalWeight={Weight:P1} EntryThreshold={Entry:F5}",
            string.Join(",", _capitalPerSymbol.Select(kv => $"{kv.Key}={kv.Value:P1}=${(basis*kv.Value):N0}")),
            _timeframe, _interval.TotalSeconds, basis, _capitalPerSymbol.Values.Sum(), _config.EntryThreshold);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                try
                {
                    await ProcessAllSymbolsAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "FundingAlphaPaperHostedService tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAllSymbolsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var runner = scope.ServiceProvider.GetRequiredService<FundingAlphaBacktestRunner>();

        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning("Funding tick skipped: PaperPortfolio.InitialBalance=0 (waiting for OKX balance sync).");
            return;
        }
        foreach (var (symbol, weight) in _capitalPerSymbol)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                var capital = basis * weight;
                await ProcessSymbolAsync(symbol, capital, marketData, runner, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Funding tick failed for {Symbol}", symbol);
            }
        }

        _logger.LogInformation(
            "Funding paper portfolio: Cash=${Cash:N2} Equity=${Equity:N2} Closed={Closed} (paper-aggregate)",
            _portfolio.Cash, _portfolio.TotalEquity, _portfolio.ClosedPositions.Count);
    }

    private async Task ProcessSymbolAsync(string symbol, decimal capital, IMarketDataService marketData, FundingAlphaBacktestRunner runner, CancellationToken ct)
    {
        var candles = await marketData.GetCandlesAsync(symbol, _candleLookback, "Crypto", _timeframe).ConfigureAwait(false);
        if (candles == null || candles.Count < 60)
        {
            _logger.LogDebug("Funding {Symbol} insufficient candles ({Count})", symbol, candles?.Count ?? 0);
            return;
        }

        var result = await runner.RunAsync(symbol, candles, _config, capital, ct).ConfigureAwait(false);

        var lastSeen = _lastSeenExitPerSymbol[symbol];
        // Skip ForceClose trades — they are a backtest artifact (force-close any open position
        // at the end of the candle window). In live execution that position would still be
        // open. Each tick re-runs the backtest with a new last bar, generating a NEW
        // ForceClose with a NEW exit time → would be mirrored as a "new trade" every tick
        // and double-count the same logical position. Real exit reasons (TakeProfit,
        // StopLoss, FundingNormalized, TimeStop) have stable exit times across ticks.
        var newTrades = result.Trades
            .Where(t => t.ExitTimeUtc > lastSeen && !string.Equals(t.ExitReason, "ForceClose", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.ExitTimeUtc)
            .ToList();
        if (newTrades.Count == 0) return;

        foreach (var trade in newTrades)
        {
            MirrorTradeToPortfolio(symbol, trade);
            _lastSeenExitPerSymbol[symbol] = trade.ExitTimeUtc;
        }

        var totalPnl = newTrades.Sum(t => t.NetPnl);
        _logger.LogInformation(
            "Funding {Symbol} mirrored {N} new closed trade(s); cumulative PnL this tick=${PnL:N2}",
            symbol, newTrades.Count, totalPnl);

        if (_alerts is not null)
        {
            var sev = totalPnl >= 0 ? "success" : "warning";
            var icon = totalPnl >= 0 ? "🟢" : "🔴";
            _ = _alerts.PublishAsync(
                sev,
                "Funding",
                $"{icon} {symbol} closed {newTrades.Count} funding trade(s)",
                $"Net ${totalPnl:N2} · last reason: {newTrades[^1].ExitReason}",
                CancellationToken.None);
        }
    }

    private void MirrorTradeToPortfolio(string symbol, FundingAlphaTrade trade)
    {
        var position = new PaperPosition
        {
            Symbol = symbol,
            Side = trade.Side,
            EntryPrice = trade.EntryPrice,
            CurrentPrice = trade.ExitPrice,
            Quantity = trade.Qty,
            EntryTime = trade.EntryTimeUtc,
            ExitTime = trade.ExitTimeUtc,
            RealizedPnL = trade.NetPnl,
            ExitReason = trade.ExitReason,
            Reasoning = $"Funding @entry={trade.FundingAtEntry:F5} @exit={trade.FundingAtExit:F5}",
            StrategyTag = "FundingAlpha",
            RegimeTag = "FundingMeanReversion",
            ExecutionMode = "PAPER",
            PromotionStage = "Paper",
            NetFunding = trade.FundingAtEntry
        };

        lock (_portfolio)
        {
            _portfolio.ClosedPositions.Add(position);
            _portfolio.Cash += trade.NetPnl;
            _portfolio.DailyPnL += trade.NetPnl;
            _portfolio.EquityCurve.Add(_portfolio.TotalEquity);
        }

        _logger.LogInformation(
            "FUNDING TRADE [{Symbol}] {Side} entryAt={EntryTs:u} exitAt={ExitTs:u} PnL=${PnL:N2} reason={Reason}",
            symbol, trade.Side, trade.EntryTimeUtc, trade.ExitTimeUtc, trade.NetPnl, trade.ExitReason);
    }

    private static decimal ParseDecimal(string? raw, decimal fallback)
        => decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
