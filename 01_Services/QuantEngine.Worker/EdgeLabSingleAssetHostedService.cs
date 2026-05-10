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

            // [Sprint 5 — 2026-05-10] B3 fix: Telegram alert on every LIVE fill, not
            // just dry-run cycles. With 13 strategies firing concurrently the operator
            // needs surface-level awareness when a real order lands. Best-effort —
            // alert publish failure must not poison the order pipeline.
            if (_alerts is not null && trade is not null)
            {
                try
                {
                    var dirIcon = signal.Side == TradeSide.Buy ? "[BUY]" : "[SELL]";
                    var notional = signal.Quantity * signal.Price;
                    var body = $"{dirIcon} {signal.Symbol} qty={signal.Quantity:F6} @ {signal.Price:F4} " +
                               $"notional=${notional:N2} status={trade.Status} fees=${trade.TotalFeesPaid:F4} " +
                               $"strategy={_strategy} weight={_strategyWeight:P1}";
                    await _alerts.PublishAsync(
                        "info", $"EdgeLab[{_strategy}]",
                        $"EdgeLab LIVE fill — {signal.Symbol}",
                        body, default).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "EdgeLabSingleAsset: live-fill Telegram publish failed"); }
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
