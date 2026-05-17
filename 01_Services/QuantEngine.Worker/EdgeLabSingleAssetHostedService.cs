using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Application.Diagnostics;
using ThatsMyQuant.Application.Strategy.EdgeLab;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Single-asset (directional) EdgeLab signal consumer. Added 2026-05-08.
///
/// Mirrors <see cref="EdgeLabSignalHostedService"/>'s execution pattern but
/// for a single signed position rather than a beta-neutral basket. Uses the
/// new <see cref="EdgeLabSingleAssetReader"/> and validates that
/// <c>metadata.strategy_kind == "single_asset"</c>.
///
/// Order submission wired 2026-05-08 (sprint Day 6 task T-1). Mirrors XS
/// service's per-symbol delta logic but for exactly 1 symbol.
/// </summary>
public sealed class EdgeLabSingleAssetHostedService : BackgroundService
{
    private readonly ILogger<EdgeLabSingleAssetHostedService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly EdgeLabSingleAssetOptions _options;
    private readonly EdgeLabStrategyOverride _override;
    private readonly string _strategy;
    private readonly TimeSpan _timeframe;
    private readonly PaperPortfolio _portfolio;
    private readonly EdgeLabDryRunLedger _dryRunLedger;
    private readonly EdgeLabSyntheticPortfolio _syntheticPortfolio;
    private readonly EdgeLabReconciliationLedger _reconciliationLedger;
    private readonly IOperatorAlertPublisher? _alerts;

    private DateTimeOffset? _lastAppliedAsOfBar;
    private decimal _cumulativeHypotheticalPnL;
    private DateTime _lastFreshSignalUtc = DateTime.UtcNow;
    private DateTime _lastStaleAlertUtc = DateTime.MinValue;

    // [Sprint 2 — 2026-05-10] Per-strategy state.
    private readonly decimal _strategyWeight;
    private readonly int _staleAlertHours;
    private EdgeLabPromotionGate.Decision _lastGateDecision = new(EdgeLabHybridMode.DryRun, "init");

    // Optional: present iff Signal Diagnostics module is wired in DI. Operator-facing
    // observability only — never gates EdgeLab cycle decisions. Fire-and-forget.
    private readonly ISignalDiagnosticsRegistry? _diagnostics;

    public EdgeLabSingleAssetHostedService(
        ILogger<EdgeLabSingleAssetHostedService> logger,
        IServiceProvider serviceProvider,
        EdgeLabSingleAssetOptions options,
        string strategy,
        TimeSpan timeframe,
        PaperPortfolio portfolio,
        EdgeLabDryRunLedger dryRunLedger,
        EdgeLabSyntheticPortfolio syntheticPortfolio,
        EdgeLabReconciliationLedger reconciliationLedger,
        IOperatorAlertPublisher? alerts = null,
        EdgeLabStrategyOverride? strategyOverride = null,
        ISignalDiagnosticsRegistry? diagnostics = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options;
        _strategy = strategy;
        _timeframe = timeframe;
        _portfolio = portfolio;
        _dryRunLedger = dryRunLedger;
        _syntheticPortfolio = syntheticPortfolio;
        _reconciliationLedger = reconciliationLedger;
        _alerts = alerts;
        _diagnostics = diagnostics;
        _cumulativeHypotheticalPnL = _reconciliationLedger.ReadLast()?.CumulativePnLUsd ?? 0m;

        // [Sprint 2] Resolve per-strategy override, falling back to equal-share defaults.
        _override = strategyOverride ?? options.GetEffectiveOverride(strategy);
        var n = options.Strategies?.Count ?? 1;
        _strategyWeight = _override.Weight ?? (n > 0 ? 1m / n : 1m);
        if (_strategyWeight < 0m) _strategyWeight = 0m;
        if (_strategyWeight > 1m) _strategyWeight = 1m;
        _staleAlertHours = _override.StaleAlertHours ?? options.StaleAlertHours;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("EdgeLabSingleAsset[{Strategy}] disabled.", _strategy);
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.InputDir))
        {
            _logger.LogWarning(
                "EdgeLabSingleAsset[{Strategy}]: InputDir not configured. Idling.", _strategy);
            return;
        }

        // [Sprint 2] Boot-time gate evaluation. Logs effective mode + reason once at startup.
        // Re-evaluated per cycle in ApplyCycleAsync so soft-promote → live transitions are picked
        // up automatically as paper cycles accumulate (no restart required).
        var declaredMode = _override.HybridMode ?? _options.HybridMode;
        var bootCycles = _reconciliationLedger.CountEntries();
        _lastGateDecision = EdgeLabPromotionGate.Resolve(
            declaredMode, _override.PromotionStatus, bootCycles, _override.MinPaperCyclesForLive);

        if (_lastGateDecision.EffectiveMode == EdgeLabHybridMode.Off)
        {
            _logger.LogInformation(
                "EdgeLabSingleAsset[{Strategy}]: gate → off ({Reason}). Idling.",
                _strategy, _lastGateDecision.Reason);
            return;
        }

        var pollSeconds = Math.Max(5, _options.PollIntervalSeconds);
        var interval = TimeSpan.FromSeconds(pollSeconds);
        _logger.LogInformation(
            "EdgeLabSingleAsset[{Strategy}] starting. DeclaredMode={Declared} EffectiveMode={Effective} " +
            "Reason='{Reason}' Weight={Weight:P1} PromotionStatus={Status} StaleAlertH={Stale} " +
            "PaperCycles={Cycles}/{MinCycles} Timeframe={Tf} Poll={Poll}s",
            _strategy, declaredMode, _lastGateDecision.EffectiveMode, _lastGateDecision.Reason,
            _strategyWeight, _override.PromotionStatus, _staleAlertHours,
            bootCycles, _override.MinPaperCyclesForLive, _timeframe, pollSeconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                try { await TickAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "EdgeLabSingleAsset[{Strategy}] tick failed.", _strategy);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var result = EdgeLabSingleAssetReader.Read(
            _options.InputDir, _strategy, now, _timeframe);

        if (!result.IsOk)
        {
            _logger.LogDebug(
                "EdgeLabSingleAsset[{Strategy}]: status={Status} reason={Reason}",
                _strategy, result.Status, result.FailureReason);
            CheckLongStale();
            return;
        }

        _lastFreshSignalUtc = DateTime.UtcNow;

        var doc = result.Document!;
        var asOfBar = doc.AsOfBar!.Value;

        // [2026-05-14 Quantlab paused-edge mechanism — TMQ_PAUSED_EDGES_SPEC.md]
        // [2026-05-15 cleanup gap fix] — original implementation skipped paused signals
        // but left ORPHAN open positions on OKX (opened pre-pause). DOGE long was sitting
        // -$11 unrealized with no SL, no rebalance, no recovery path. The cleanup branch
        // below detects existing OKX position via the DbToPortfolioSync mirror and submits
        // a one-shot close order through the standard execution path.
        if (doc.Metadata?.Paused == true)
        {
            // Symbol to clean up = the single target in this strategy's signal.
            var pausedSymbol = result.Symbol;

            // Check OKX mirror for an open position on this symbol (LIVE_OKX marker).
            PaperPosition? openPosition = null;
            if (!string.IsNullOrEmpty(pausedSymbol))
            {
                lock (_portfolio)
                {
                    openPosition = _portfolio.OpenPositions.FirstOrDefault(p =>
                        string.Equals(p.Symbol, pausedSymbol, StringComparison.OrdinalIgnoreCase)
                        && p.ExecutionMode == DbToPortfolioSyncHostedService.OkxMirrorMarker
                        && p.Quantity > 0m);
                }
            }

            if (openPosition is null)
            {
                // Clean — no orphan to mop up. Standard SKIP with dedupe.
                if (_lastAppliedAsOfBar != asOfBar)
                {
                    _logger.LogInformation(
                        "[SKIP] EdgeLabSingleAsset[{Strategy}] PAUSED by Quantlab as_of={AsOf} reason='{Reason}' (no orphan position to close)",
                        _strategy, asOfBar, doc.Metadata.PausedReason ?? "(no reason given)");
                    _lastAppliedAsOfBar = asOfBar;
                }
                return;
            }

            // Orphan position exists — attempt close-to-zero ONCE per asOfBar (idempotent).
            // Per-asOfBar dedupe so a failed close attempt isn't retried every poll, only
            // each fresh signal cycle (~1h for 1h strategies). If the close fails, next
            // fresh signal will retry.
            if (_lastAppliedAsOfBar == asOfBar) return;
            _lastAppliedAsOfBar = asOfBar;

            _logger.LogWarning(
                "[CLEANUP] EdgeLabSingleAsset[{Strategy}] PAUSED but ORPHAN POSITION exists on {Symbol} (qty={Qty} side={Side}) — submitting close order.",
                _strategy, openPosition.Symbol, openPosition.Quantity, openPosition.Side);

            await TryClosePausedOrphanAsync(openPosition, doc.Metadata.PausedReason, ct).ConfigureAwait(false);
            return;
        }

        if (_lastAppliedAsOfBar == asOfBar)
            return;

        // Timing gate: wait until as_of_bar + timeframe has passed.
        var executionBar = asOfBar + _timeframe;
        if (now < executionBar)
        {
            _logger.LogDebug(
                "EdgeLabSingleAsset[{Strategy}]: signal as_of_bar={AsOf} not yet eligible " +
                "(waiting until {ExecBar}, {Wait} remaining).",
                _strategy, asOfBar, executionBar, executionBar - now);
            return;
        }

        await ApplyCycleAsync(doc, result.Symbol!, result.TargetWeight, ct).ConfigureAwait(false);
        _lastAppliedAsOfBar = asOfBar;
    }

    /// <summary>
    /// 2026-05-08 sprint task T-1 wiring complete. Mirrors XS service's
    /// per-cycle pattern but for a single signed position:
    ///   1. Snapshot mark for the single symbol
    ///   2. Compute hypothetical period PnL via synthetic book
    ///   3. Read current position (synthetic in dry-run, exchange in live)
    ///   4. Compute delta = target_notional - current_notional
    ///   5. Submit order if |delta| > MinDeltaUsd
    ///   6. Append to dry-run ledger + reconciliation ledger
    ///   7. Commit synthetic book to next cycle's targets
    /// </summary>
    private async Task ApplyCycleAsync(
        EdgeLabSignalDocument doc, string symbol, double weight, CancellationToken ct)
    {
        var basis = _portfolio.InitialBalance;
        if (basis <= 0m)
        {
            _logger.LogWarning(
                "EdgeLabSingleAsset[{Strategy}]: PaperPortfolio.InitialBalance=0, " +
                "skipping cycle as_of={AsOf}.",
                _strategy, doc.AsOfBar);
            return;
        }

        // 2026-05-11: Basis-drift hard veto. If the runtime basis exceeds the config
        // Paper:InitialBalance by more than 1.5× the ExchangeBalanceSync hasn't yet
        // re-anchored the stale snapshot — skip the cycle. Prevents the historical
        // failure mode where boot-time race conditions sized orders off a $96K
        // pre-pivot snapshot instead of the intended $2K cycle basis (over-sized
        // ada_breakout fired at $11.6K notional vs the $240 target).
        var configuration = _serviceProvider.GetService<IConfiguration>();
        var configBasisRaw = configuration?["Paper:InitialBalance"];
        if (decimal.TryParse(configBasisRaw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var configBasis)
            && configBasis > 0m
            && basis > configBasis * 1.5m)
        {
            _logger.LogWarning(
                "EdgeLabSingleAsset[{Strategy}]: BASIS DRIFT VETO — runtime ${Runtime:N2} > " +
                "1.5× config ${Config:N2}. Most likely cause: stale PaperPortfolio snapshot " +
                "that ExchangeBalanceSync hasn't re-anchored yet. Skipping cycle to prevent " +
                "over-sized order. as_of={AsOf}.",
                _strategy, basis, configBasis, doc.AsOfBar);
            return;
        }

        var leverage = doc.Metadata?.Leverage ?? 1.0;

        // [Sprint 2] Re-resolve the promotion gate every cycle so a soft-promote
        // strategy auto-promotes to live when its paper-cycle counter crosses
        // MinPaperCyclesForLive — no restart needed. We log the transition only
        // when the effective mode changes (avoids per-cycle log spam).
        var declaredMode = _override.HybridMode ?? _options.HybridMode;
        var paperCycles = _reconciliationLedger.CountEntries();

        // [Sprint 6] Kill-switch consumption. The KillSwitch state is singleton-scoped
        // and JSON-backed, updated hourly by EdgeLabKillSwitchHostedService with
        // per-strategy multipliers {1.0, 0.5, 0.0}. We read it on every cycle so a
        // transition propagates to sizing within one EdgeLab tick rather than waiting
        // for restart. Resolved via root provider (singleton) — no DI scope needed.
        var killState = _serviceProvider.GetService<EdgeLabKillSwitchState>();
        var killMultiplier = killState is not null ? killState.GetMultiplier(_strategy) : 1.0m;
        if (killMultiplier == EdgeLabKillSwitch.HardKillMultiplier)
        {
            // Hard kill = treat declared mode as dry-run regardless of gate.
            declaredMode = "dry-run";
        }

        // Push the latest contract thresholds to the kill switch service so its next
        // hourly evaluation uses them. Cheap dict update; no IO.
        var ks = _serviceProvider.GetService<EdgeLabKillSwitchHostedService>();
        ks?.UpdateThresholds(_strategy,
            doc.Metadata?.ExpectedCycPerYr,
            doc.Metadata?.KillSwitchRealizedSharpe30c,
            doc.Metadata?.KillSwitchRealizedSharpeHard);

        var gateDecision = EdgeLabPromotionGate.Resolve(
            declaredMode, _override.PromotionStatus, paperCycles, _override.MinPaperCyclesForLive);
        if (gateDecision.EffectiveMode != _lastGateDecision.EffectiveMode)
        {
            _logger.LogInformation(
                "EdgeLabSingleAsset[{Strategy}] gate transition: {Prev} → {Next} ({Reason}, paper_cycles={Cycles})",
                _strategy, _lastGateDecision.EffectiveMode, gateDecision.EffectiveMode,
                gateDecision.Reason, paperCycles);
        }
        _lastGateDecision = gateDecision;

        var mode = TranslateGateMode(gateDecision.EffectiveMode);
        using var scope = _serviceProvider.CreateScope();
        var exchange = scope.ServiceProvider.GetRequiredService<IExchangeService>();
        var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
        var nowUtc = DateTime.UtcNow;

        // Step 1 — snapshot current mark for the single symbol
        var mark = await GetMarkPriceAsync(symbol, exchange, marketData, ct).ConfigureAwait(false);
        if (mark <= 0m)
        {
            _logger.LogWarning(
                "EdgeLabSingleAsset[{Strategy}]: no mark price for {Sym}, skipping cycle.",
                _strategy, symbol);
            return;
        }
        var currentMarks = new Dictionary<string, decimal>(StringComparer.Ordinal) { [symbol] = mark };

        // Step 2 — period PnL on prior held position
        var (periodPnL, perSymbolPnL) = _syntheticPortfolio.ComputePeriodPnL(currentMarks);
        _cumulativeHypotheticalPnL += periodPnL;

        // Step 3 — current notional
        var currentNotional = 0m;
        if (mode == HybridMode.DryRun)
        {
            foreach (var (sym, pos) in _syntheticPortfolio.GetState().Positions)
            {
                if (sym == symbol && currentMarks.TryGetValue(sym, out var px) && px > 0m)
                    currentNotional = pos.SignedQty * px;
            }
        }
        else if (mode == HybridMode.Live)
        {
            var positions = scope.ServiceProvider.GetRequiredService<IExchangePositionReader>();
            var openSnapshots = await positions.GetOpenPositionsAsync(ct).ConfigureAwait(false);
            foreach (var p in openSnapshots)
            {
                if (p.Symbol != symbol) continue;
                var signed = (p.IsLong ? 1m : -1m) * p.Quantity * mark;
                currentNotional += signed;
            }
        }

        // Step 4 — target notional (signal weight × strategy weight × kill-switch mult × basis × leverage).
        // [Sprint 2] _strategyWeight scales the signal's per-symbol weight to this strategy's
        // share of the basis. Without this, multiple co-active strategies all sized to 100%
        // of basis would over-allocate.
        // [Sprint 6] killMultiplier ∈ {1.0, 0.5, 0.0} from realized-Sharpe kill switch:
        //   1.0  = full Kelly (default)
        //   0.5  = half Kelly (realized 30c Sharpe < soft threshold, e.g. -0.3)
        //   0.0  = hard kill (realized 30c Sharpe < hard threshold, e.g. -1.0). Position
        //          targets to zero → next rebalance flattens; declaredMode also forced
        //          to dry-run above to avoid sending real orders.
        var effectiveStrategyWeight = _strategyWeight * killMultiplier;
        var targetNotional = basis * effectiveStrategyWeight * (decimal)(weight * leverage);
        var deltaNotional = targetNotional - currentNotional;

        // [Sprint 7 — 2026-05-11] Per-symbol notional cap evaluation.
        // Defends against multi-strategy concentration on a single underlying
        // (current BTC stack: fee_panic + stablecoin_30d + rv_breakout + btc_breakout_4h_long
        // sum to weight 0.31 → $930 at $2K basis × 1.5lev when all co-fire).
        // Live mode → skip submit when breached. DryRun → log-only warning.
        decimal? capFraction = null;
        if (_options.PerSymbolCap is { Count: > 0 } caps
            && caps.TryGetValue(symbol, out var capValue))
        {
            capFraction = capValue;
        }

        // Sum absolute notional across ALL open positions on the symbol (cross-strategy).
        // Live mode reads from the exchange (single source of truth, no per-strategy attribution
        // available from OKX). DryRun reads from the shared SyntheticPortfolio.
        decimal totalSymbolNotional = 0m;
        if (capFraction is not null)
        {
            if (mode == HybridMode.Live)
            {
                var positions = scope.ServiceProvider.GetRequiredService<IExchangePositionReader>();
                var allOpen = await positions.GetOpenPositionsAsync(ct).ConfigureAwait(false);
                foreach (var p in allOpen)
                {
                    if (!string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase)) continue;
                    var signedNotional = (p.IsLong ? 1m : -1m) * p.Quantity * mark;
                    totalSymbolNotional += signedNotional;
                }
            }
            else if (mode == HybridMode.DryRun)
            {
                // Synthetic accounting is per-strategy instance, so for DryRun the cross-strategy
                // aggregate is not directly available. Use this-strategy's currentNotional as
                // the best-effort proxy — produces a conservative WouldBreach decision only when
                // this strategy alone exceeds the cap. The warning is still informative.
                totalSymbolNotional = currentNotional;
            }
        }

        var capDecision = EdgeLabPerSymbolCapEvaluator.Evaluate(
            symbol: symbol,
            basis: basis,
            targetNotionalUsd: targetNotional,
            thisStrategyCurrentNotionalUsd: currentNotional,
            totalSymbolNotionalUsd: totalSymbolNotional,
            capFraction: capFraction,
            isLive: mode == HybridMode.Live);

        if (capDecision.Action == EdgeLabPerSymbolCapAction.WouldBreachInDryRun
            || capDecision.Action == EdgeLabPerSymbolCapAction.InvalidCap)
        {
            _logger.LogWarning(
                "EdgeLabSingleAsset[{Strategy}] per-symbol-cap WARN sym={Sym} action={Action} reason={Reason}",
                _strategy, symbol, capDecision.Action, capDecision.Reason);
        }

        if (capDecision.ShouldBlockSubmit)
        {
            _logger.LogWarning(
                "EdgeLabSingleAsset[{Strategy}] per-symbol-cap SKIP sym={Sym} cap=${Cap:F2} would_be=${Would:F2} headroom=${Head:F2} target=${Target:F2} delta=${Delta:F2}",
                _strategy, symbol, capDecision.CapUsd, capDecision.WouldBeTotalAbsUsd,
                capDecision.HeadroomUsd, targetNotional, deltaNotional);
            if (_alerts is not null)
            {
                _ = _alerts.PublishAsync(
                    "warn", $"EdgeLab[{_strategy}]",
                    $"PerSymbolCap skip — {symbol}",
                    $"cap ${capDecision.CapUsd:F2}, would_be ${capDecision.WouldBeTotalAbsUsd:F2}, headroom ${capDecision.HeadroomUsd:F2}. {capDecision.Reason}",
                    ct);
            }
            return;
        }

        // Update synthetic position regardless of whether we issue an order
        // (held position with no churn still earns hypothetical PnL).
        var newSynthetic = new Dictionary<string, EdgeLabSyntheticPosition>(StringComparer.Ordinal);
        if (mark > 0m)
        {
            var signedQty = targetNotional / mark;
            newSynthetic[symbol] = new EdgeLabSyntheticPosition(signedQty, mark, (decimal)weight);
        }

        // Step 5 — submit order if delta exceeds threshold
        var orderType = ParseOrderType(_options.OrderType);
        var ledgerEntries = new List<EdgeLabDryRunEntry>();
        var orderResult = (Symbol: symbol, Ok: true, Err: (string?)null, Submitted: false);

        if (Math.Abs(deltaNotional) >= _options.MinDeltaUsd)
        {
            var qty = Math.Abs(deltaNotional) / mark;
            var side = deltaNotional > 0 ? TradeSide.Buy : TradeSide.Sell;

            // [2026-05-16 PreEntryRiskGate wiring] EdgeLab rebalance can both ADD to position
            // (open/increase) or REDUCE (close-toward-zero). Only gate-check when the delta
            // would INCREASE absolute exposure — closing trades should always be allowed
            // (reducing risk is good, even if existing position is at the cap).
            var isIncreasingExposure = Math.Abs(targetNotional) > Math.Abs(currentNotional);
            if (isIncreasingExposure)
            {
                try
                {
                    var preGate = _serviceProvider.GetService<PreEntryRiskGate>();
                    if (preGate is not null)
                    {
                        // For increase, the relevant notional cap-check is the target (post-delta) size.
                        var preGateDecision = preGate.CheckEntry(symbol, Math.Abs(targetNotional));
                        if (!preGateDecision.Allowed)
                        {
                            _logger.LogWarning(
                                "EdgeLabSingleAsset[{Strategy}] {Symbol} entry BLOCKED by PreEntryRiskGate: {Reason}",
                                _strategy, symbol, preGateDecision.RejectReason);
                            return;
                        }
                    }
                }
                catch (Exception preGateEx)
                {
                    _logger.LogDebug(preGateEx, "EdgeLabSingleAsset[{Strategy}] PreEntryRiskGate evaluation threw; allowing entry.", _strategy);
                }
            }

            var signal = new Signal
            {
                Source = "EdgeLabSingleAsset",
                StrategyTag = _strategy,
                Symbol = symbol,
                Side = side,
                Price = mark,
                Quantity = qty,
                OrderType = orderType,
                Exchange = "OKX",
                MarketType = MarketType.Crypto,
                Confidence = 100,
                Reasoning = $"EdgeLab single-asset rebalance to weight={weight:+0.00;-0.00;0} (delta=${deltaNotional:N2})",
                Timestamp = nowUtc,
            };
            // [Sprint 3 — 2026-05-10] Full Quantlab signal context capture.
            // Propagated via signal.Metadata → consumed by OkxExecutionService and
            // (eventually) persisted on the resulting Trade row. Lets the daily
            // realized-vs-backtest divergence digest (Sprint 4) join fills back to
            // the factor/z_score context that drove them, plus gives the post-mortem
            // operator clear "why did this fire" attribution years later when the
            // hypothesis pre-reg doc has rotated out of memory.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            signal.Metadata["edge_lab_as_of_bar"] = doc.AsOfBar!.Value.ToString("o");
            signal.Metadata["edge_lab_target_weight"] = weight.ToString("R", inv);
            signal.Metadata["edge_lab_strategy_kind"] = "single_asset";
            signal.Metadata["edge_lab_strategy_weight"] = _strategyWeight.ToString("F4", inv);
            signal.Metadata["edge_lab_promotion_status"] = _override.PromotionStatus;
            if (doc.FactorValues != null && doc.FactorValues.TryGetValue(symbol, out var fv))
                signal.Metadata["edge_lab_factor_value"] = fv.ToString("R", inv);
            if (doc.Metadata?.Factor != null)
                signal.Metadata["edge_lab_factor_name"] = doc.Metadata.Factor;
            if (doc.Metadata?.Leverage is double lev)
                signal.Metadata["Leverage"] = lev.ToString("R", inv);
            if (doc.Metadata?.FeeBpsRoundTrip is double feeBps)
                signal.Metadata["edge_lab_fee_bps_round_trip"] = feeBps.ToString("R", inv);

            // [Sprint 6 — 2026-05-10] Quantlab audit contract (TMQ_INTEGRATION_CONTRACT.md)
            // Each signal carries the backtest-expectation snapshot. We propagate the full
            // panel as signal.Metadata so OkxExecutionService can persist it on the Trade
            // (BacktestExpectedPnL is the headline number; the rest joins to the trade row
            // via signal.Metadata for offline analysis).
            if (doc.Metadata?.ExpectedSharpeAnn is double sh)
                signal.Metadata["edge_lab_expected_sharpe_ann"] = sh.ToString("R", inv);
            if (doc.Metadata?.ExpectedMeanPct is double mn)
                signal.Metadata["edge_lab_expected_mean_pct"] = mn.ToString("R", inv);
            if (doc.Metadata?.ExpectedStdPct is double sd)
                signal.Metadata["edge_lab_expected_std_pct"] = sd.ToString("R", inv);
            if (doc.Metadata?.ExpectedCycPerYr is double cy)
                signal.Metadata["edge_lab_expected_cyc_per_yr"] = cy.ToString("R", inv);
            if (doc.Metadata?.ExpectedWinRate is double wr)
                signal.Metadata["edge_lab_expected_win_rate"] = wr.ToString("R", inv);
            if (doc.Metadata?.BacktestNCycles is int nC)
                signal.Metadata["edge_lab_backtest_n_cycles"] = nC.ToString(inv);
            if (!string.IsNullOrWhiteSpace(doc.Metadata?.StressTestScore))
                signal.Metadata["edge_lab_stress_test_score"] = doc.Metadata.StressTestScore;
            if (doc.Metadata?.KillSwitchRealizedSharpe30c is double k30)
                signal.Metadata["edge_lab_kill_switch_30cyc"] = k30.ToString("R", inv);
            if (doc.Metadata?.KillSwitchRealizedSharpeHard is double kh)
                signal.Metadata["edge_lab_kill_switch_hard"] = kh.ToString("R", inv);

            // Headline: per-cycle expected PnL in dollars = mean_pct/100 × notional. Captured
            // on Trade.BacktestExpectedPnL by OkxExecutionService for the daily divergence digest.
            if (doc.Metadata?.ExpectedMeanPct is double meanPctForPnL && Math.Abs(deltaNotional) > 0m)
            {
                var expectedDollarPnL = (decimal)(meanPctForPnL / 100.0) * Math.Abs(deltaNotional);
                signal.Metadata["BacktestExpectedPnL"] = expectedDollarPnL.ToString("F4", inv);
            }

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

            var submitResult = await SubmitAsync(exchange, signal, mode).ConfigureAwait(false);
            orderResult = (submitResult.Symbol, submitResult.Ok, submitResult.Err, true);
        }

        // Step 6 — reconciliation entry every cycle (period PnL on held positions matters)
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
            catch (Exception ex) { _logger.LogWarning(ex, "EdgeLabSingleAsset: reconciliation append failed."); }

            // Step 7 — commit synthetic book
            try { _syntheticPortfolio.Commit(doc.AsOfBar!.Value, nowUtc, newSynthetic); }
            catch (Exception ex) { _logger.LogWarning(ex, "EdgeLabSingleAsset: synthetic commit failed."); }

            if (ledgerEntries.Count > 0)
            {
                try { _dryRunLedger.AppendCycle(ledgerEntries); }
                catch (Exception ex) { _logger.LogWarning(ex, "EdgeLabSingleAsset: dry-run ledger append failed."); }
            }

            // Telegram alert
            if (_alerts is not null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"as_of {doc.AsOfBar:u} | {_strategy} | basis ${basis:N2}");
                var pnlIcon = periodPnL >= 0 ? "[+]" : "[-]";
                sb.AppendLine($"{pnlIcon} period_pnl=${periodPnL:+0.00;-0.00;0.00} cum=${_cumulativeHypotheticalPnL:+0.00;-0.00;0.00}");
                if (ledgerEntries.Count == 0)
                {
                    sb.AppendLine($"(no rebalance — |delta=${deltaNotional:N2}| < MinDelta=${_options.MinDeltaUsd:N2})");
                }
                else
                {
                    var e = ledgerEntries[0];
                    var dirIcon = e.Side == "Buy" ? "[BUY]" : "[SELL]";
                    sb.AppendLine($"{dirIcon} {e.Symbol} qty={e.Qty:F6} @ {e.Price:F4} notional=${e.NotionalUsd:N2} (w={weight:+0.00;-0.00;0})");
                }
                await _alerts.PublishAsync(
                    "info", $"EdgeLab[{_strategy}]",
                    $"EdgeLab single-asset DRY-RUN ({(orderResult.Submitted ? "would-submit" : "no rebalance")})",
                    sb.ToString(), ct).ConfigureAwait(false);
            }
        }

        var modeTag = mode == HybridMode.DryRun ? " [DRY-RUN]" : string.Empty;
        _logger.LogInformation(
            "EdgeLabSingleAsset[{Strategy}] cycle mode={Mode} as_of={AsOf} sym={Sym} weight={W:F4} " +
            "mark={Mark:F4} target_notional=${Target:N2} current_notional=${Current:N2} " +
            "delta=${Delta:N2} submitted={Submitted}{ModeTag}",
            _strategy, mode, doc.AsOfBar, symbol, weight, mark,
            targetNotional, currentNotional, deltaNotional, orderResult.Submitted, modeTag);

        // Operator-facing diagnostics: 1 publish per cycle summarising the decision.
        // Read-only path; never gates anything. Decision logic mirrors the actual cycle outcome.
        PublishCycleDiagnostic(symbol, weight, killMultiplier, deltaNotional,
            _options.MinDeltaUsd, orderResult, currentNotional);
    }

    /// <summary>
    /// Publishes a single per-cycle summary to the diagnostics registry. EdgeLab differs from
    /// funding/pairs because it makes ONE rebalance decision per tick (no per-symbol gate
    /// branching), so we publish once at the end of <see cref="ApplyCycleAsync"/> with the
    /// final outcome.
    /// </summary>
    /// <remarks>
    /// Schema convention: <c>CurrentValue</c> = target_weight (what the signal asked for),
    /// <c>EntryThreshold</c> = 0 (any non-zero weight is a fire candidate; the actual gate is
    /// the delta-vs-MinDeltaUsd check). <c>DistanceToTriggerPct</c> = |delta| / MinDeltaUsd × 100
    /// — i.e. how close we are to crossing the rebalance threshold.
    /// </remarks>
    private void PublishCycleDiagnostic(
        string symbol,
        double weight,
        decimal killMultiplier,
        decimal deltaNotional,
        decimal minDeltaUsd,
        (string Symbol, bool Ok, string? Err, bool Submitted) orderResult,
        decimal currentNotional)
    {
        if (_diagnostics is null) return;

        try
        {
            var distance = minDeltaUsd > 0m
                ? Math.Abs(deltaNotional) / minDeltaUsd * 100m
                : 0m;

            // Decision priority (top-down):
            //   1. KillSwitch hard kill (mult=0.0) → SuppressedByKillSwitch
            //   2. Order submitted but failed → SkippedByRunner (with error)
            //   3. Order submitted ok → Fired
            //   4. Target weight = 0 → Flat (intentional flat signal from Quantlab)
            //   5. |delta| < MinDelta → BelowThreshold (held position, no churn needed)
            //      (NearTrigger auto-promotion still applies at ≥80%)
            //   6. Otherwise → BelowThreshold (catch-all; rare)
            SignalDecision decision;
            string? reason = null;

            if (killMultiplier == EdgeLabKillSwitch.HardKillMultiplier)
            {
                decision = SignalDecision.SuppressedByKillSwitch;
                reason = $"HardKill mult={killMultiplier:F1} weight={weight:+0.00;-0.00;0}";
            }
            else if (orderResult.Submitted && !orderResult.Ok)
            {
                decision = SignalDecision.SkippedByRunner;
                reason = $"OrderFailed: {orderResult.Err ?? "unknown"}";
            }
            else if (orderResult.Submitted)
            {
                decision = SignalDecision.Fired;
                reason = $"Rebalanced delta=${deltaNotional:N2} weight={weight:+0.00;-0.00;0}";
            }
            else if (weight == 0.0)
            {
                decision = SignalDecision.Flat;
                reason = "Target weight = 0 (Quantlab flat signal)";
            }
            else
            {
                // Held position, no rebalance needed (delta below MinDelta). Helper auto-
                // promotes to NearTrigger if we're close to needing a rebalance.
                decision = SignalDecision.BelowThreshold;
                reason = $"Hold weight={weight:+0.00;-0.00;0} (|delta|=${Math.Abs(deltaNotional):N2} < min=${minDeltaUsd:N2})";
            }
            if (decision == SignalDecision.BelowThreshold && distance >= 80m && distance < 100m)
                decision = SignalDecision.NearTrigger;

            // Half-Kelly (0.5×) doesn't suppress entirely; surface it in the reason so the
            // operator sees WHY a fired rebalance might be smaller than expected.
            if (killMultiplier == EdgeLabKillSwitch.HalfKellyMultiplier && decision == SignalDecision.Fired)
                reason = (reason ?? "") + " [HalfKelly 0.5×]";

            var snap = new SignalDiagnosticSnapshot(
                UtcTimestamp: DateTime.UtcNow,
                Tier: "EdgeLab:single_asset",
                StrategyTag: _strategy,
                Key: symbol,
                CurrentValue: (decimal)weight,
                EntryThreshold: 0m, // EdgeLab has no symmetric Z/funding threshold; fire gate is delta-based
                ExitThreshold: null,
                StopThreshold: null,
                DistanceToTriggerPct: distance,
                Decision: decision,
                SkipOrSuppressReason: reason,
                RegimeContext: null, // EdgeLab doesn't carry regime metadata into per-cycle diagnostics
                HasOpenPosition: currentNotional != 0m,
                EstimatedNotionalIfFired: Math.Abs(deltaNotional));

            _diagnostics.Publish(snap);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Diagnostics publish failed for EdgeLab[{Strategy}] {Symbol}", _strategy, symbol);
        }
    }

    /// <summary>
    /// [2026-05-15 paused-edge cleanup] Submit a close order for an orphan position left
    /// behind when an edge transitioned to PAUSED state. Uses the standard
    /// OkxExecutionService path with ReduceOnly=true so the order cannot accidentally
    /// open a reverse position if the size goes wrong. Telegram alert on success/failure.
    ///
    /// Called once per fresh asOfBar cycle (already deduped by _lastAppliedAsOfBar).
    /// If the close fails, next cycle (1h later for 1h strategies, 4h for 4h) retries —
    /// or operator can flatten manually on OKX UI.
    /// </summary>
    private async Task TryClosePausedOrphanAsync(
        PaperPosition openPosition,
        string? pausedReason,
        CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var exchange = scope.ServiceProvider.GetRequiredService<IExchangeService>();

            // Close = opposite side, same quantity.
            var closeSide = openPosition.Side == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;
            var price = openPosition.CurrentPrice > 0m ? openPosition.CurrentPrice : openPosition.EntryPrice;

            var signal = new Signal
            {
                Source = "EdgeLabSingleAsset",
                StrategyTag = _strategy,
                Symbol = openPosition.Symbol,
                Side = closeSide,
                Price = price,
                Quantity = openPosition.Quantity,
                OrderType = OrderType.Market,                  // urgent flatten — taker by intent
                Exchange = "OKX",
                MarketType = MarketType.Crypto,
                Confidence = 100,
                Reasoning = $"Paused-edge cleanup: closing orphan position (paused reason: {pausedReason ?? "n/a"})",
                Timestamp = DateTime.UtcNow,
            };
            // ReduceOnly so OKX rejects the order if it would flip direction
            // (defensive against position-size race with our mirror).
            signal.Metadata["ReduceOnly"] = "true";
            signal.Metadata["edge_lab_paused_cleanup"] = "true";

            var trade = await exchange.ExecuteOrderAsync(signal).ConfigureAwait(false);

            var actuallyFilled = trade is not null
                && trade.Status == ThatsMyQuant.Contracts.Enums.TradeStatus.Executed
                && trade.FilledQuantity > 0m;

            if (actuallyFilled)
            {
                _logger.LogWarning(
                    "[CLEANUP-OK] EdgeLabSingleAsset[{Strategy}] orphan close FILLED on {Symbol}: {Side} qty={Qty:F6} @ {Px:F4} fee={Fee:F4}",
                    _strategy, openPosition.Symbol, closeSide, trade!.FilledQuantity, trade.EntryPrice, trade.TotalFeesPaid);

                if (_alerts is not null)
                {
                    try
                    {
                        await _alerts.PublishAsync(
                            "info", $"EdgeLab[{_strategy}]",
                            $"✅ Paused-edge cleanup — {openPosition.Symbol} CLOSED",
                            $"Orphan position from paused edge closed via market.\n" +
                            $"Side: {closeSide} qty={trade.FilledQuantity:F6} @ {trade.EntryPrice:F4}\n" +
                            $"Fee: ${trade.TotalFeesPaid:F4}\n" +
                            $"Pause reason: {pausedReason}",
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception alertEx) { _logger.LogDebug(alertEx, "EdgeLabSingleAsset: cleanup-OK alert publish failed"); }
                }
            }
            else
            {
                _logger.LogError(
                    "[CLEANUP-FAIL] EdgeLabSingleAsset[{Strategy}] orphan close NOT executed on {Symbol} (status={Status} filled={Filled}). Will retry next fresh cycle.",
                    _strategy, openPosition.Symbol, trade?.Status, trade?.FilledQuantity);

                if (_alerts is not null)
                {
                    try
                    {
                        await _alerts.PublishAsync(
                            "warning", $"EdgeLab[{_strategy}]",
                            $"⚠️ Paused-edge cleanup — {openPosition.Symbol} close NOT executed",
                            $"Tried to close orphan position from paused edge, order not executed.\n" +
                            $"Status: {trade?.Status} filled={trade?.FilledQuantity:F6}\n" +
                            $"Will retry next fresh signal cycle. Manual close on OKX UI also OK.\n" +
                            $"Symbol: {openPosition.Symbol} qty={openPosition.Quantity:F6} side={openPosition.Side}",
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception alertEx) { _logger.LogDebug(alertEx, "EdgeLabSingleAsset: cleanup-FAIL alert publish failed"); }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[CLEANUP-EX] EdgeLabSingleAsset[{Strategy}] orphan close threw on {Symbol}",
                _strategy, openPosition.Symbol);
        }
    }

    private async Task<(string Symbol, bool Ok, string? Err)> SubmitAsync(
        IExchangeService exchange, Signal signal, HybridMode mode)
    {
        if (mode == HybridMode.DryRun)
        {
            _logger.LogDebug("EdgeLabSingleAsset WOULD submit: {Sym} {Side} qty={Qty:N6} @ {Px:N4}",
                signal.Symbol, signal.Side, signal.Quantity, signal.Price);
            return (signal.Symbol, true, null);
        }
        try
        {
            var trade = await exchange.ExecuteOrderAsync(signal).ConfigureAwait(false);
            _logger.LogInformation(
                "EdgeLabSingleAsset order filled: {Sym} {Side} qty={Qty:N6} @ {Px:N4} status={Status} strategy={Strategy}",
                signal.Symbol, signal.Side, signal.Quantity, signal.Price, trade?.Status, _strategy);

            // [2026-05-15 BUG FIX] Skip ALL operator alerts when the trade did not actually
            // execute. Previously: any non-null trade (even status=Pending or FilledQty=0)
            // produced an "OPENED Buy ETHUSDT live fill" Telegram message → operator opens
            // OKX, sees no new position, panics. Root cause: cancel-verify or market-retry
            // returned the Trade scaffold but the actual fill never landed.
            // Fix: alerts only when Status == Executed AND FilledQuantity > 0.
            var actuallyFilled = trade is not null
                && trade.Status == ThatsMyQuant.Contracts.Enums.TradeStatus.Executed
                && trade.FilledQuantity > 0m;
            if (trade is not null && !actuallyFilled)
            {
                _logger.LogWarning(
                    "EdgeLabSingleAsset[{Strategy}] {Sym} {Side} submitted but NOT executed (status={Status} filled={Filled}); SKIPPING green-OPENED alert. Reconcile loop will sync real state.",
                    _strategy, signal.Symbol, signal.Side, trade.Status, trade.FilledQuantity);

                // [2026-05-15] Still surface to Telegram with HONEST text — operator
                // needs visibility but should NOT see "🟢 OPENED" when nothing opened.
                if (_alerts is not null)
                {
                    try
                    {
                        var dirIcon = signal.Side == TradeSide.Buy ? "[BUY]" : "[SELL]";
                        var body = $"{dirIcon} {signal.Symbol} qty={signal.Quantity:F6} @ {signal.Price:F4} " +
                                   $"status={trade.Status} filled={trade.FilledQuantity:F6} " +
                                   $"strategy={_strategy}\n\n" +
                                   "Order submitted but NOT executed yet. Possible: post-only timeout " +
                                   "still in flight, MARKET fallback rejected, or cancel-verify aborted. " +
                                   "Reconcile loop + DbToPortfolioSync (60s) will reflect real OKX state.";
                        await _alerts.PublishAsync(
                            "warning", $"EdgeLab[{_strategy}]",
                            $"⚠️ EdgeLab order SUBMITTED — not yet executed ({signal.Symbol})",
                            body, default).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "EdgeLabSingleAsset: not-executed Telegram publish failed"); }
                }
            }

            // [2026-05-16 per-strategy attribution] Append to fill-attribution ledger so
            // DbToPortfolioSync can recover the strategy tag when mirroring closed positions
            // (OKX positions-history doesn't carry attribution).
            if (trade is not null && actuallyFilled)
            {
                try
                {
                    var attributionLedger = _serviceProvider.GetService<EdgeLabFillAttributionLedger>();
                    attributionLedger?.Append(
                        timestampUtc: DateTime.UtcNow,
                        symbol: signal.Symbol,
                        strategy: _strategy,
                        side: signal.Side,
                        qty: trade.FilledQuantity,
                        price: trade.EntryPrice,
                        venueOrderId: trade.Id.ToString());
                }
                catch (Exception attribEx)
                {
                    _logger.LogDebug(attribEx, "EdgeLabSingleAsset[{Strategy}]: attribution ledger append failed", _strategy);
                }
            }

            // [Sprint 5 — 2026-05-10] B3 fix: Telegram alert on every LIVE fill, not
            // just dry-run cycles. With 13 strategies firing concurrently the operator
            // needs surface-level awareness when a real order lands. Best-effort —
            // alert publish failure must not poison the order pipeline.
            // [2026-05-15] Gated on actuallyFilled (status=Executed + qty>0).
            if (_alerts is not null && trade is not null && actuallyFilled)
            {
                try
                {
                    var dirIcon = signal.Side == TradeSide.Buy ? "[BUY]" : "[SELL]";
                    var notional = trade.FilledQuantity * trade.EntryPrice;
                    var body = $"{dirIcon} {signal.Symbol} qty={trade.FilledQuantity:F6} @ {trade.EntryPrice:F4} " +
                               $"notional=${notional:N2} status={trade.Status} fees=${trade.TotalFeesPaid:F4} " +
                               $"strategy={_strategy} weight={_strategyWeight:P1}";
                    await _alerts.PublishAsync(
                        "info", $"EdgeLab[{_strategy}]",
                        $"EdgeLab LIVE fill — {signal.Symbol}",
                        body, default).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "EdgeLabSingleAsset: live-fill Telegram publish failed"); }
            }

            // 2026-05-11: Publish PositionOpenedNotification — TelegramPositionPushHandler
            // subscribes via MediatR and sends the "🟢 OPENED" Telegram push. Without
            // this, EdgeLab fills only hit the DbOperatorAlertPublisher (DB row + SignalR)
            // — the operator sees nothing in Telegram. Matches FundingHybridExecutor pattern.
            // Fire-and-forget so a slow Telegram round-trip doesn't block the cycle.
            // [2026-05-15] Gated on actuallyFilled — was firing "🟢 OPENED" even when order
            // status=Pending or qty=0 (cancel-verify or market-retry returned scaffold).
            if (trade is not null && actuallyFilled)
            {
                try
                {
                    var mediator = _serviceProvider.GetService<MediatR.IMediator>();
                    if (mediator is not null)
                    {
                        decimal slPx = 0m, tpPx = 0m;
                        if (signal.Metadata.TryGetValue("StopLossPx", out var slRaw))
                            decimal.TryParse(slRaw, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out slPx);
                        if (signal.Metadata.TryGetValue("TakeProfitPx", out var tpRaw))
                            decimal.TryParse(tpRaw, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out tpPx);
                        var note = new ThatsMyQuant.Contracts.Notifications.PositionOpenedNotification(
                            Symbol: signal.Symbol,
                            StrategyTag: _strategy,
                            RegimeTag: $"weight={_strategyWeight:P0}",
                            ExitPolicyTag: tpPx > 0m && slPx > 0m
                                ? $"TP={tpPx:F4}/SL={slPx:F4}"
                                : "HoldOpen",
                            ExecutionMode: "OKX_DEMO",
                            Side: signal.Side,
                            EntryPrice: signal.Price,
                            Quantity: signal.Quantity,
                            StopLoss: slPx,
                            TakeProfit: tpPx,
                            EdgeScore: 0m,
                            Confidence: 75m,
                            Reasoning: $"EdgeLab[{_strategy}] live fill",
                            EntryTimeUtc: DateTime.UtcNow,
                            CorrelationId: trade.Id.ToString());
                        _ = mediator.Publish(note);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "EdgeLabSingleAsset: PositionOpenedNotification publish failed");
                }
            }
            return (signal.Symbol, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EdgeLabSingleAsset order failed for {Sym} {Side} qty={Qty} strategy={Strategy}",
                signal.Symbol, signal.Side, signal.Quantity, _strategy);

            // Critical: failed live fill is a real-money / real-execution-path concern. Always
            // surface to operator immediately, irrespective of cooldown.
            if (_alerts is not null)
            {
                try
                {
                    await _alerts.PublishAsync(
                        "critical", $"EdgeLab[{_strategy}]",
                        $"EdgeLab LIVE order FAILED — {signal.Symbol}",
                        $"side={signal.Side} qty={signal.Quantity:F6} px={signal.Price:F4} err={ex.Message}",
                        default).ConfigureAwait(false);
                }
                catch (Exception alertEx) { _logger.LogDebug(alertEx, "EdgeLabSingleAsset: live-fail Telegram publish failed"); }
            }
            return (signal.Symbol, false, ex.Message);
        }
    }

    private async Task<decimal> GetMarkPriceAsync(
        string symbol, IExchangeService exchange,
        IMarketDataService marketData, CancellationToken ct)
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
            // Single-asset strategies usually run on 1h (event-driven) or 4h.
            // Use a generic 1h fallback — the hosted service config controls cadence
            // independently of mark price resolution.
            var candles = await marketData.GetCandlesAsync(symbol, 1, "Crypto", "1h").ConfigureAwait(false);
            if (candles is { Count: > 0 }) return candles[^1].Close;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EdgeLabSingleAsset: candle fallback failed for {Sym}", symbol);
        }
        return 0m;
    }

    private static OrderType ParseOrderType(string raw) =>
        Enum.TryParse<OrderType>(raw, ignoreCase: true, out var t) ? t : OrderType.Limit;

    private static HybridMode NormalizeMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return HybridMode.DryRun;
        return raw.Trim().ToLowerInvariant() switch
        {
            "off" => HybridMode.Off,
            "live" => HybridMode.Live,
            _ => HybridMode.DryRun,
        };
    }

    /// <summary>
    /// [Sprint 2] Bridge from the gate's enum (in Application namespace, used for
    /// pure-function unit testing) to the worker-internal enum used downstream.
    /// Both share Off / DryRun / Live values; this is a 1:1 mapping.
    /// </summary>
    private static HybridMode TranslateGateMode(EdgeLabHybridMode m) => m switch
    {
        EdgeLabHybridMode.Off => HybridMode.Off,
        EdgeLabHybridMode.Live => HybridMode.Live,
        _ => HybridMode.DryRun,
    };

    private void CheckLongStale()
    {
        var hoursSinceFresh = (DateTime.UtcNow - _lastFreshSignalUtc).TotalHours;
        if (hoursSinceFresh < _staleAlertHours) return;
        var hoursSinceAlert = (DateTime.UtcNow - _lastStaleAlertUtc).TotalHours;
        if (hoursSinceAlert < _staleAlertHours) return;
        _logger.LogWarning(
            "EdgeLabSingleAsset[{Strategy}]: no fresh signal for {Hours:F1}h " +
            "(threshold {Threshold}h). Quantlab cron may be down or strategy idle.",
            _strategy, hoursSinceFresh, _staleAlertHours);
        _lastStaleAlertUtc = DateTime.UtcNow;
    }
}
