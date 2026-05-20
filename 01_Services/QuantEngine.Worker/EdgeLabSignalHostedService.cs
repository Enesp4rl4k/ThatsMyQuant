using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Consumes EdgeLab's <c>signals/&lt;strategy&gt;/latest.json</c> bridge file
/// and rebalances the OKX paper book to match <c>target_weights</c>.
///
/// Contract reference: Quantlab repo <c>signals/README.md</c> §"Bot Integration
/// Contract". This service implements all 7 non-negotiable points; every
/// failure mode in the README's table holds positions and alerts rather than
/// acting on partial input.
///
/// Sized off <see cref="PaperPortfolio.InitialBalance"/> (synced from OKX demo
/// balance by <see cref="ExchangeBalanceSyncHostedService"/>) so a fresh worker
/// inherits the live account size automatically. Sum |target_weights| = 2.0
/// → 200% gross, 100% long + 100% short, beta-neutral.
///
/// Polls every <see cref="EdgeLabSignalOptions.PollIntervalSeconds"/> (default 60s)
/// and dedups acted-on cycles by <c>as_of_bar</c> — over-polling is free.
/// </summary>
public sealed class EdgeLabSignalHostedService : BackgroundService
{
    private readonly ILogger<EdgeLabSignalHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly EdgeLabSignalOptions _options;
    private readonly PaperPortfolio _portfolio;
    private readonly IOperatorAlertPublisher? _alerts;
    private readonly EdgeLabDryRunLedger _dryRunLedger;
    private readonly EdgeLabSyntheticPortfolio _syntheticPortfolio;
    private readonly EdgeLabReconciliationLedger _reconciliationLedger;

    private DateTimeOffset? _lastAppliedAsOfBar;
    private decimal _cumulativeHypotheticalPnL;
    // 2026-05-07: long-stale warning state. Tracks when we last saw a fresh signal
    // (or service start). Fires Telegram every StaleAlertHours when no signal arrives.
    // Diagnoses both real failures (cron broken) AND honest registry-empty idle state
    // (5/3 retraction scenario) — operator decides which.
    private DateTime _lastFreshSignalUtc = DateTime.UtcNow;
    private DateTime _lastStaleAlertUtc = DateTime.MinValue;

    public EdgeLabSignalHostedService(
        ILogger<EdgeLabSignalHostedService> logger,
        IServiceProvider serviceProvider,
        IOptions<EdgeLabSignalOptions> options,
        PaperPortfolio portfolio,
        EdgeLabDryRunLedger dryRunLedger,
        EdgeLabSyntheticPortfolio syntheticPortfolio,
        EdgeLabReconciliationLedger reconciliationLedger,
        IOperatorAlertPublisher? alerts = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _portfolio = portfolio;
        _dryRunLedger = dryRunLedger;
        _syntheticPortfolio = syntheticPortfolio;
        _reconciliationLedger = reconciliationLedger;
        _alerts = alerts;
        // Hydrate cumulative PnL from disk so a worker restart preserves running
        // total — promotion gate compares cumulative across N≥30 cycles.
        _cumulativeHypotheticalPnL = _reconciliationLedger.ReadLast()?.CumulativePnLUsd ?? 0m;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("EdgeLabSignalHostedService disabled via config.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.InputDir))
        {
            _logger.LogWarning("EdgeLabSignalHostedService: EdgeLab:InputDir not configured. Service will idle.");
            return;
        }

        var mode = NormalizeMode(_options.HybridMode);
        if (mode == HybridMode.Off)
        {
            _logger.LogInformation("EdgeLabSignalHostedService: HybridMode=off, idling.");
            return;
        }

        var pollSeconds = Math.Max(5, _options.PollIntervalSeconds);
        var interval = TimeSpan.FromSeconds(pollSeconds);
        _logger.LogInformation(
            "EdgeLabSignalHostedService starting. Mode={Mode} Strategy={Strategy} Timeframe={Tf} InputDir={Dir} Poll={Poll}s",
            mode, _options.Strategy, _options.Timeframe, _options.InputDir, pollSeconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EdgeLabSignalHostedService tick failed.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = EdgeLabSignalReader.Read(_options, now);
        if (!result.IsOk)
        {
            await HandleFailureAsync(result, ct).ConfigureAwait(false);
            await CheckLongStaleAsync(ct).ConfigureAwait(false);
            return;
        }

        // Successful read — refresh the freshness clock.
        _lastFreshSignalUtc = DateTime.UtcNow;

        var doc = result.Document!;
        var asOfBar = doc.AsOfBar!.Value;

        // [2026-05-14 Quantlab paused-edge mechanism — TMQ_PAUSED_EDGES_SPEC.md]
        // Honour metadata.paused=true (Quantlab degraded-edge gate). Log once per
        // unique asOfBar to avoid every-poll spam; dedupe via the same sentinel.
        if (doc.Metadata?.Paused == true)
        {
            if (_lastAppliedAsOfBar != asOfBar)
            {
                _logger.LogInformation(
                    "[SKIP] EdgeLabSignal[{Strategy}] PAUSED by Quantlab as_of={AsOf} reason='{Reason}'",
                    _options.Strategy, asOfBar, doc.Metadata.PausedReason ?? "(no reason given)");
                _lastAppliedAsOfBar = asOfBar;
            }
            return;
        }

        // Dedup — never re-apply the same cycle. Process restart wipes this in
        // memory but Bot Integration Contract pt 7 (timing gate) still prevents
        // re-firing inside the same bar; the worst case is one extra rebalance
        // at the next 4h boundary, which is harmless against current positions.
        if (_lastAppliedAsOfBar == asOfBar)
            return;

        // Contract pt 7 — fill at as_of_bar + 1 timeframe open.
        if (!EdgeLabSignalReader.TryParseTimeframe(_options.Timeframe, out var tf))
        {
            _logger.LogError("EdgeLab: unparseable timeframe '{Tf}', cannot enforce timing gate.", _options.Timeframe);
            return;
        }
        var executionBar = asOfBar + tf;
        if (now < executionBar)
        {
            _logger.LogDebug(
                "EdgeLab: signal for as_of_bar={AsOf} not yet eligible (waiting for {ExecBar}, {Wait} remaining).",
                asOfBar, executionBar, executionBar - now);
            return;
        }

        await ApplyCycleAsync(doc, ct).ConfigureAwait(false);
    }

    private async Task ApplyCycleAsync(EdgeLabSignalDocument doc, CancellationToken ct)
    {
        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning(
                "EdgeLab: PaperPortfolio.InitialBalance=0 — waiting for ExchangeBalanceSyncHostedService to seed real OKX demo balance. Skipping cycle.");
            return;
        }

        // 2026-05-17: Apply per-strategy Kelly haircut from signal metadata.
        // Quantlab handoff §2: emitter writes metadata.kelly_haircut per cycle so
        // sizing can be tuned without re-deploying TMQ. Missing → fall back to the
        // configured default (default 0.5 = half-Kelly) so a hypothetical Quantlab
        // regression degrades gracefully instead of silently sizing full-Kelly.
        var kellyHaircutRaw = doc.Metadata?.KellyHaircut ?? _options.DefaultKellyHaircut;
        var kellyHaircut = (decimal)Math.Clamp(kellyHaircutRaw, 0.0, 1.0);
        if (kellyHaircut <= 0m)
        {
            _logger.LogInformation(
                "EdgeLab: strategy={Strategy} kelly_haircut={Kh:F2} — sizing-disabled cycle, skipping.",
                doc.Strategy, kellyHaircut);
            return;
        }

        var mode = NormalizeMode(_options.HybridMode);
        using var scope = _serviceProvider.CreateScope();
        var exchange = scope.ServiceProvider.GetRequiredService<IExchangeService>();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();

        // Step 1 — snapshot current marks for EVERY symbol in the universe upfront.
        // We need them in three places (period PnL math, delta sizing, and persisted
        // for next cycle). Doing this once keeps a consistent price moment and is
        // cheaper than re-querying inside the per-symbol loop.
        var currentMarks = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var symbol in doc.TargetWeights!.Keys)
        {
            var px = await GetMarkPriceAsync(symbol, exchange, marketData, ct).ConfigureAwait(false);
            if (px > 0m) currentMarks[symbol] = px;
        }

        // Step 2 — compute period PnL: hypothetical mark-to-mark return on positions
        // we were holding at the END of the previous cycle, evaluated at this cycle's
        // marks. This is the ex-ante reconciliation signals/README.md §Reconciliation
        // describes; the only meaningful way to validate consumer behavior in dry-run.
        var (periodPnL, perSymbolPnL) = _syntheticPortfolio.ComputePeriodPnL(currentMarks);
        _cumulativeHypotheticalPnL += periodPnL;

        // Step 3 — current notional baseline. In dry-run we read from synthetic book
        // (own positions only). In live, real OKX position reader (TODO: switch to
        // synthetic when per-strategy clOrdId tagging is wired so we don't double-size
        // against Funding/Pairs).
        var currentNotional = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (mode == HybridMode.DryRun)
        {
            foreach (var (sym, pos) in _syntheticPortfolio.GetState().Positions)
            {
                if (!currentMarks.TryGetValue(sym, out var px) || px <= 0m) continue;
                currentNotional[sym] = pos.SignedQty * px;
            }
        }
        else if (mode == HybridMode.Live)
        {
            var positions = scope.ServiceProvider.GetRequiredService<IExchangePositionReader>();
            var openSnapshots = await positions.GetOpenPositionsAsync(ct).ConfigureAwait(false);
            foreach (var p in openSnapshots)
            {
                if (!currentMarks.TryGetValue(p.Symbol, out var px) || px <= 0m) continue;
                var signed = (p.IsLong ? 1m : -1m) * p.Quantity * px;
                currentNotional[p.Symbol] = currentNotional.GetValueOrDefault(p.Symbol) + signed;
            }
        }

        var orderType = ParseOrderType(_options.OrderType);
        var orderTasks = new List<Task<(string Symbol, bool Ok, string? Err)>>();
        var ledgerEntries = new List<EdgeLabDryRunEntry>();
        var newSynthetic = new Dictionary<string, EdgeLabSyntheticPosition>(StringComparer.Ordinal);
        var grossTarget = 0m;
        var nowUtc = DateTime.UtcNow;

        foreach (var (symbol, weight) in doc.TargetWeights!)
        {
            if (ct.IsCancellationRequested) break;

            var targetNotional = basis * (decimal)weight * kellyHaircut;
            grossTarget += Math.Abs(targetNotional);

            // Persist target position for next cycle's period-PnL math regardless of
            // whether we issue a delta order this cycle (a held position with no churn
            // still earns hypothetical PnL).
            if (currentMarks.TryGetValue(symbol, out var symMark) && symMark > 0m)
            {
                var signedQty = targetNotional / symMark;  // signed (negative weight → short)
                newSynthetic[symbol] = new EdgeLabSyntheticPosition(signedQty, symMark, (decimal)weight);
            }

            var current = currentNotional.GetValueOrDefault(symbol, 0m);
            var deltaNotional = targetNotional - current;
            if (Math.Abs(deltaNotional) < _options.MinDeltaUsd)
                continue;

            if (!currentMarks.TryGetValue(symbol, out var mark) || mark <= 0m)
            {
                _logger.LogWarning("EdgeLab: no mark price for {Sym}, skipping delta order.", symbol);
                continue;
            }

            var qty = Math.Abs(deltaNotional) / mark;
            var side = deltaNotional > 0 ? TradeSide.Buy : TradeSide.Sell;

            var signal = new Signal
            {
                Source = "EdgeLab",
                StrategyTag = "realized_vol_4h_xs_reverse",
                Symbol = symbol,
                Side = side,
                Price = mark,
                Quantity = qty,
                OrderType = orderType,
                Exchange = "OKX",
                MarketType = MarketType.Crypto,
                Confidence = 100,
                Reasoning = $"EdgeLab rebalance to weight={weight:+0.00;-0.00;0} (delta=${deltaNotional:N0})",
                Timestamp = nowUtc,
            };
            signal.Metadata["edge_lab_as_of_bar"] = doc.AsOfBar!.Value.ToString("o");
            signal.Metadata["edge_lab_target_weight"] = weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            signal.Metadata["kelly_haircut"] = kellyHaircut.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (mode == HybridMode.DryRun)
            {
                ledgerEntries.Add(new EdgeLabDryRunEntry(
                    TimestampUtc: nowUtc,
                    AsOfBar: doc.AsOfBar!.Value,
                    Symbol: symbol,
                    Side: side.ToString(),
                    Qty: qty,
                    Price: mark,
                    NotionalUsd: Math.Abs(deltaNotional),
                    TargetWeight: (decimal)weight,
                    DeltaUsd: deltaNotional,
                    Basis: basis));
            }

            orderTasks.Add(SubmitAsync(exchange, signal, mode));
        }

        var results = await Task.WhenAll(orderTasks).ConfigureAwait(false);
        var ok = results.Count(r => r.Ok);
        var failed = results.Length - ok;

        // Reconciliation entry — written every cycle (even when zero deltas), because
        // period PnL on held positions is the whole point of tracking this.
        if (mode == HybridMode.DryRun)
        {
            var prevAsOf = _syntheticPortfolio.GetState().LastCycleAsOfBar;
            var reconEntry = new EdgeLabReconciliationEntry(
                TimestampUtc: nowUtc,
                ThisCycleAsOfBar: doc.AsOfBar!.Value,
                PrevCycleAsOfBar: prevAsOf,
                PeriodPnLUsd: periodPnL,
                CumulativePnLUsd: _cumulativeHypotheticalPnL,
                Basis: basis,
                PositionsHeld: newSynthetic.Count(p => p.Value.SignedQty != 0m),
                PerSymbolPnL: perSymbolPnL);
            try { _reconciliationLedger.Append(reconEntry); }
            catch (Exception ex) { _logger.LogWarning(ex, "EdgeLab: reconciliation ledger append failed."); }

            // Atomically advance the synthetic book to this cycle's targets. Next cycle
            // computes period PnL against THESE positions and marks.
            try { _syntheticPortfolio.Commit(doc.AsOfBar!.Value, nowUtc, newSynthetic); }
            catch (Exception ex) { _logger.LogWarning(ex, "EdgeLab: synthetic portfolio commit failed."); }
        }

        // Persist dry-run intent + push detailed Telegram alert. Both happen outside
        // SubmitAsync so the JSONL row count matches the alert's order count exactly.
        if (mode == HybridMode.DryRun && ledgerEntries.Count > 0)
        {
            try { _dryRunLedger.AppendCycle(ledgerEntries); }
            catch (Exception ex) { _logger.LogWarning(ex, "EdgeLab: dry-run ledger append failed."); }
        }
        if (mode == HybridMode.DryRun && _alerts is not null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"as_of {doc.AsOfBar:u} · {ledgerEntries.Count} intended orders · gross ${grossTarget:N0} · basis ${basis:N0}");
            // Period + cumulative hypothetical PnL. On the very first cycle there's no
            // prior position so periodPnL = 0; we still print it so operator gets the
            // mental model from day one.
            var pnlIcon = periodPnL >= 0 ? "📈" : "📉";
            sb.AppendLine($"{pnlIcon} Hypothetical PnL: period ${periodPnL:+0.00;-0.00;0.00} · cum ${_cumulativeHypotheticalPnL:+0.00;-0.00;0.00}");
            if (ledgerEntries.Count == 0)
            {
                sb.AppendLine("(no rebalance trades — all symbols within MinDeltaUsd of synthetic book)");
            }
            else
            {
                foreach (var e in ledgerEntries)
                {
                    var emoji = e.Side == "Buy" ? "🟢" : "🔴";
                    sb.AppendLine(
                        $"{emoji} {e.Side,-4} {e.Symbol,-9} qty={e.Qty:F6} @ ~{e.Price:F4} " +
                        $"notional≈${e.NotionalUsd:N0} (w={e.TargetWeight:+0.00;-0.00;0})");
                }
            }
            var titleSuffix = ledgerEntries.Count == 0 ? "no rebalance" : $"{ledgerEntries.Count} would-be orders";
            await _alerts.PublishAsync(
                "info", "EdgeLab",
                $"🟡 EdgeLab DRY-RUN cycle ({titleSuffix})",
                sb.ToString(),
                ct).ConfigureAwait(false);
        }

        // Commit dedup cursor only after we attempted the cycle. A partial-fail
        // tick still advances — retrying the same cycle would double-up the
        // already-filled deltas. Operator alert tells us to investigate.
        _lastAppliedAsOfBar = doc.AsOfBar;

        var modeTag = mode == HybridMode.DryRun ? " [DRY-RUN, no OKX traffic]" : string.Empty;
        var summary =
            $"EdgeLab cycle mode={mode} as_of={doc.AsOfBar:u} orders={results.Length} ok={ok} failed={failed} " +
            $"basis=${basis:N0} gross_target=${grossTarget:N0}{modeTag}";
        _logger.LogInformation(summary);

        // Live mode pushes a single summary alert; dry-run already pushed the
        // detailed per-order alert above, so don't double-notify.
        if (_alerts is not null && mode == HybridMode.Live)
        {
            var sev = failed == 0 ? "info" : "warning";
            await _alerts.PublishAsync(sev, "EdgeLab", "EdgeLab rebalance applied", summary, ct).ConfigureAwait(false);
        }
    }

    private async Task<(string Symbol, bool Ok, string? Err)> SubmitAsync(IExchangeService exchange, Signal signal, HybridMode mode)
    {
        if (mode == HybridMode.DryRun)
        {
            // No OKX call. Per-order detail goes to the JSONL ledger + consolidated
            // Telegram alert in ApplyCycleAsync; logging here would just duplicate.
            _logger.LogDebug("EdgeLab WOULD submit: {Sym} {Side} qty={Qty:N6} @ ~{Px:N4}",
                signal.Symbol, signal.Side, signal.Quantity, signal.Price);
            return (signal.Symbol, true, null);
        }

        try
        {
            var trade = await exchange.ExecuteOrderAsync(signal).ConfigureAwait(false);
            _logger.LogInformation(
                "EdgeLab order filled: {Sym} {Side} qty={Qty:N6} @ ~{Px:N4} status={Status}",
                signal.Symbol, signal.Side, signal.Quantity, signal.Price, trade?.Status);
            return (signal.Symbol, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EdgeLab order failed for {Sym} {Side} qty={Qty}", signal.Symbol, signal.Side, signal.Quantity);
            return (signal.Symbol, false, ex.Message);
        }
    }

    private async Task<decimal> GetMarkPriceAsync(
        string symbol,
        IExchangeService exchange,
        IMarketDataService marketData,
        CancellationToken ct)
    {
        if (exchange.TryGetLatestOrderBook(symbol, out var book)
            && book.Bids is { Length: > 0 } && book.Asks is { Length: > 0 })
        {
            var bid = book.Bids[0].Price;
            var ask = book.Asks[0].Price;
            if (bid > 0m && ask > 0m) return (bid + ask) / 2m;
        }
        try
        {
            var candles = await marketData.GetCandlesAsync(symbol, 1, "Crypto", _options.Timeframe).ConfigureAwait(false);
            if (candles is { Count: > 0 }) return candles[^1].Close;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EdgeLab: candle fallback failed for {Sym}", symbol);
        }
        return 0m;
    }

    /// <summary>
    /// 2026-05-07: long-stale warning. Fires Telegram every StaleAlertHours (default 24)
    /// when no fresh signal has been read. Catches:
    ///   - Real failures (cron broken, schema mismatch sustained)
    ///   - Honest idle states (registry empty post-retraction, awaiting new approval)
    /// Operator decides which by checking Quantlab registry.
    /// </summary>
    private async Task CheckLongStaleAsync(CancellationToken ct)
    {
        var staleAlertHours = Math.Max(1, _options.StaleAlertHours);
        var staleThreshold = TimeSpan.FromHours(staleAlertHours);
        var staleness = DateTime.UtcNow - _lastFreshSignalUtc;

        if (staleness < staleThreshold) return;

        // Cooldown so we don't spam every 60s — once per StaleAlertHours.
        var sinceLastAlert = DateTime.UtcNow - _lastStaleAlertUtc;
        if (sinceLastAlert < staleThreshold) return;

        _lastStaleAlertUtc = DateTime.UtcNow;
        var hours = (int)staleness.TotalHours;
        _logger.LogWarning(
            "EdgeLab signal STALE for {Hours}h — no fresh signal since {Last:o}. " +
            "Likely cause: STRATEGY_REGISTRY empty (Quantlab side). Approve a new strategy or set EdgeLab.Enabled=false.",
            hours, _lastFreshSignalUtc);

        if (_alerts is not null)
        {
            await _alerts.PublishAsync(
                "warning", "EdgeLab",
                $"⚠️ EdgeLab signal stale {hours}h",
                $"No fresh signal in {hours}h (since {_lastFreshSignalUtc:u}).\n" +
                $"Strategy: {_options.Strategy}\n" +
                $"InputDir: {_options.InputDir}\n\n" +
                "Check Quantlab STRATEGY_REGISTRY:\n" +
                "  - Empty? Awaiting new strategy approval (Sprint 4.3 Phase 2).\n" +
                "  - Populated? Cron broken — check signal_cron_*.log files.\n" +
                "Set EdgeLab.Enabled=false in appsettings if waiting indefinitely.",
                ct).ConfigureAwait(false);
        }
    }

    private async Task HandleFailureAsync(EdgeLabReadResult result, CancellationToken ct)
    {
        // Map failure → severity per signals/README.md "Failure Modes" table.
        // Schema/strategy/timeframe mismatch and bad factors are critical (probably
        // a producer regression). Stale / missing / breadth are warnings (data
        // pipeline lag — operator should look but it's recoverable).
        var (severity, kind) = result.Status switch
        {
            EdgeLabReadStatus.Missing => ("warning", "missing"),
            EdgeLabReadStatus.Stale => ("warning", "stale"),
            EdgeLabReadStatus.InsufficientBreadth => ("warning", "breadth"),
            EdgeLabReadStatus.Malformed => ("error", "malformed"),
            EdgeLabReadStatus.SchemaMismatch => ("error", "schema"),
            EdgeLabReadStatus.StrategyMismatch => ("error", "strategy"),
            EdgeLabReadStatus.TimeframeMismatch => ("error", "timeframe"),
            EdgeLabReadStatus.BadFactorValues => ("error", "factors"),
            EdgeLabReadStatus.WeightAsymmetry => ("error", "weights"),
            _ => ("warning", "unknown")
        };

        // De-spam: don't re-log a stable Missing every 60s once it's already
        // been observed once this run — log at Debug after the first hit.
        var level = result.Status == EdgeLabReadStatus.Missing
            ? Microsoft.Extensions.Logging.LogLevel.Debug
            : Microsoft.Extensions.Logging.LogLevel.Warning;
        _logger.Log(level,
            "EdgeLab signal not actionable: {Status} — {Reason}",
            result.Status, result.FailureReason);

        if (_alerts is not null && result.Status != EdgeLabReadStatus.Missing)
        {
            await _alerts.PublishAsync(
                severity, "EdgeLab",
                $"EdgeLab signal {kind}",
                result.FailureReason ?? result.Status.ToString(),
                ct).ConfigureAwait(false);
        }
    }

    private static OrderType ParseOrderType(string raw) =>
        Enum.TryParse<OrderType>(raw, ignoreCase: true, out var v) ? v : OrderType.Market;

    private static HybridMode NormalizeMode(string? raw)
    {
        var s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
        {
            "off" => HybridMode.Off,
            "live" => HybridMode.Live,
            "dryrun" or "dry-run" or "dry_run" or "" => HybridMode.DryRun,
            _ => HybridMode.DryRun, // fail safe: anything unrecognized stays out of OKX
        };
    }
}

internal enum HybridMode { Off, DryRun, Live }

