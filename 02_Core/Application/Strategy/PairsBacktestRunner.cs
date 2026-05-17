using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Standalone pairs-trading backtest simulator. Replays two aligned candle series and
/// executes mean-reversion entries/exits driven by Engle-Granger cointegration and a
/// rolling z-score of the residual spread. Isolated from the live MediatR pipeline —
/// all trade accounting is internal so it can produce a clean <see cref="BacktestResult"/>.
/// </summary>
public sealed class PairsBacktestRunner
{
    private readonly ILogger<PairsBacktestRunner> _logger;
    private readonly PairsTradingOptions _options;

    /// <summary>The runner's "live" decision on the latest bar of the most recent <see cref="Run"/>
    /// call. Used by the hybrid pairs executor to decide whether to place real OKX demo orders
    /// RIGHT NOW. Null until first Run completes.</summary>
    public PairsLiveSignal? LastLiveSignal { get; private set; }

    public PairsBacktestRunner(
        ILogger<PairsBacktestRunner> logger,
        IOptions<PairsTradingOptions>? options = null)
    {
        _logger = logger;
        _options = options?.Value ?? new PairsTradingOptions();
    }

    /// <summary>
    /// Runs a pairs backtest over two aligned candle series.
    /// </summary>
    /// <param name="candlesA">Series Y (dependent leg) — e.g. BTC</param>
    /// <param name="candlesB">Series X (independent leg) — e.g. ETH</param>
    /// <param name="startingCapital">Total capital; split between legs per hedge ratio.</param>
    /// <param name="feeRatePerLeg">Round-trip fee per leg as a fraction (0.0005 = 5 bps).</param>
    public BacktestResult Run(
        IReadOnlyList<Candle> candlesA,
        IReadOnlyList<Candle> candlesB,
        decimal startingCapital = 10_000m,
        decimal feeRatePerLeg = 0.0005m,
        decimal? leverageOverride = null,
        bool liveIsFlat = false,
        bool? requireRangingRegimeOverride = null)
    {
        // [F1 per-pair opt-in] Caller (PortfolioBacktestRunner) can force the regime
        // gate ON for a specific pair label even when the global default is OFF.
        // Null leaves the global default in place.
        bool effectiveRequireRanging = requireRangingRegimeOverride ?? _options.RequireRangingRegime;
        var aligned = AlignByTimestamp(candlesA, candlesB);
        if (aligned.Count < Math.Max(100, _options.CointegrationLookbackBars / 4))
        {
            return EmptyResult(startingCapital, aligned, "Insufficient aligned candles");
        }

        int lookback = Math.Max(60, _options.CointegrationLookbackBars);
        int zWindow = Math.Max(10, _options.ZScoreWindow);
        int rebalance = Math.Max(1, _options.RebalanceInterval);
        int cooldown = Math.Max(0, _options.SignalCooldownTicks);
        double entryZ = _options.EntryZScoreThreshold > 0 ? _options.EntryZScoreThreshold : 2.0;
        double exitZ = _options.ExitZScoreThreshold > 0 ? _options.ExitZScoreThreshold : 0.5;
        double stopZ = _options.StopLossZScoreThreshold > 0 ? _options.StopLossZScoreThreshold : 3.5;
        int maxHold = Math.Max(1, _options.MaxHoldingBars);

        PairSignalState? coint = null;
        int lastRebalanceBar = -1;
        int lastSignalBar = int.MinValue / 2;
        int cointTestCount = 0;
        int cointPassCount = 0;
        int signalArmed = 0;
        double absMaxZ = 0;
        double? previousZScore = null;

        // Captures WHY the latest bar declined to emit a WouldOpenNew signal — surfaced via
        // PairsLiveSignal.Reasoning so the hybrid executor can log "BCH-LTC skipped: velocity
        // filter zPrev=3.8 zNow=4.31" instead of silently doing nothing. Only the LAST bar's
        // reason matters for live execution; intermediate bars don't affect anything reachable.
        string? liveSkipReason = null;

        // Shadow threshold instrumentation (2026-05-02): how many bars in the lookback would
        // have hit relaxed entry thresholds? Diagnoses "are we starving for signals because
        // the regime is dead, or because 2.5σ is too tight?". Counts |z| crossings at fixed
        // thresholds — separate from the actual entryZ — so we measure raw signal supply
        // without contaminating the live decision. Threshold-independent; no PnL simulation.
        int barsWithValidZ = 0;
        int shadowAbove1_0 = 0;
        int shadowAbove1_5 = 0;
        int shadowAbove2_0 = 0;
        int shadowAbove2_5 = 0;
        int shadowAbove3_0 = 0;

        var trades = new List<BacktestTradeResult>();
        var equityCurve = new List<decimal> { startingCapital };
        decimal equity = startingCapital;
        decimal peakEquity = startingCapital;
        decimal maxDrawdown = 0m;

        PairPositionState position = PairPositionState.Flat;
        int entryBar = -1;
        double entryZScore = 0.0;
        decimal entryPriceA = 0m;
        decimal entryPriceB = 0m;
        decimal qtyA = 0m;
        decimal qtyB = 0m;
        decimal hedgeRatio = 1m;
        double bestMeanReversionProgress = 0.0;

        for (int i = 0; i < aligned.Count; i++)
        {
            var (ts, a, b) = aligned[i];

            // Rolling cointegration refresh
            if (i >= lookback && (i - lastRebalanceBar) >= rebalance)
            {
                var y = new decimal[lookback];
                var x = new decimal[lookback];
                for (int k = 0; k < lookback; k++)
                {
                    y[k] = aligned[i - lookback + k].A.Close;
                    x[k] = aligned[i - lookback + k].B.Close;
                }
                var result = EngleGrangerDetector.TestCointegration(y, x);
                var correlation = ComputeCorrelation(y, x);
                coint = new PairSignalState(result, correlation);
                lastRebalanceBar = i;
                cointTestCount++;
                bool tradeable = IsTradeable(coint.Value);
                if (tradeable) cointPassCount++;
                else
                {
                    // Diagnostic: log first failure per pair so we can see WHY IsTradeable rejected.
                    var c = coint.Value;
                    _logger.LogInformation(
                        "Coint REJECT {A}/{B} | StdDev={SD:F4} HL={HL:F1} ADF={ADF:F3} Beta={Beta:F3} Corr={Corr:F3} | thresholds: HL[{HMin}-{HMax}] Beta[{BMin}-{BMax}] Corr>={CMin} ADF<0",
                        _options.SymbolA, _options.SymbolB,
                        c.SpreadStdDev, c.HalfLife, c.AdfTestStatistic, c.HedgeRatio, c.Correlation,
                        _options.MinHalfLifeBars, _options.MaxHalfLifeBars,
                        _options.MinHedgeRatio, _options.MaxHedgeRatio,
                        _options.MinCorrelation);
                }
            }

            if (coint is null || !IsTradeable(coint.Value) || i < lookback)
            {
                equityCurve.Add(equity);
                continue;
            }
            var currentCoint = coint.Value;

            // Rolling z-score over the trailing ZScoreWindow of residuals
            decimal residual = a.Close - currentCoint.HedgeRatio * b.Close;
            if (i < zWindow) { equityCurve.Add(equity); continue; }

            decimal rollMean = 0m;
            for (int k = i - zWindow + 1; k <= i; k++)
            {
                var row = aligned[k];
                rollMean += row.A.Close - currentCoint.HedgeRatio * row.B.Close;
            }
            rollMean /= zWindow;

            decimal rollVar = 0m;
            for (int k = i - zWindow + 1; k <= i; k++)
            {
                var row = aligned[k];
                decimal r = (row.A.Close - currentCoint.HedgeRatio * row.B.Close) - rollMean;
                rollVar += r * r;
            }
            decimal rollStd = (decimal)Math.Sqrt((double)(rollVar / Math.Max(1, zWindow - 1)));
            if (rollStd <= 0m) { equityCurve.Add(equity); continue; }

            double zScore = (double)((residual - rollMean) / rollStd);
            double absZ = Math.Abs(zScore);
            if (absZ > absMaxZ) absMaxZ = absZ;
            signalArmed++;

            // Shadow threshold counters: only on bars where coint ALREADY admitted (we passed
            // the IsTradeable gate at line 128). Measures "if entryZ were X, how many bars would
            // have crossed?" — answers user's question "is edge dead or just throttled by 2.5σ?".
            barsWithValidZ++;
            if (absZ >= 1.0) shadowAbove1_0++;
            if (absZ >= 1.5) shadowAbove1_5++;
            if (absZ >= 2.0) shadowAbove2_0++;
            if (absZ >= 2.5) shadowAbove2_5++;
            if (absZ >= 3.0) shadowAbove3_0++;

            if (position != PairPositionState.Flat)
            {
                bool exit = false;
                string exitReason = "";
                double currentProgress = position == PairPositionState.ShortSpread
                    ? Math.Max(0.0, entryZScore - zScore)
                    : Math.Max(0.0, zScore - entryZScore);
                bestMeanReversionProgress = Math.Max(bestMeanReversionProgress, currentProgress);

                if (position == PairPositionState.ShortSpread && zScore <= exitZ)
                { exit = true; exitReason = "MeanReversion"; }
                else if (position == PairPositionState.LongSpread && zScore >= -exitZ)
                { exit = true; exitReason = "MeanReversion"; }

                if (!exit)
                {
                    if (position == PairPositionState.ShortSpread && zScore > stopZ)
                    { exit = true; exitReason = "StopLoss"; }
                    else if (position == PairPositionState.LongSpread && zScore < -stopZ)
                    { exit = true; exitReason = "StopLoss"; }
                }

                if (!exit)
                {
                    int barsHeld = i - entryBar;
                    double hlCap = !double.IsInfinity(currentCoint.HalfLife) && !double.IsNaN(currentCoint.HalfLife) && currentCoint.HalfLife > 0
                        ? currentCoint.HalfLife * _options.MaxHoldingHalfLifeMultiplier
                        : double.PositiveInfinity;
                    if (barsHeld > maxHold || barsHeld > hlCap)
                    { exit = true; exitReason = "MaxHoldTime"; }
                }

                if (!exit && bestMeanReversionProgress >= 0.75)
                {
                    double retreatFraction = _options.ZScoreRetreatExitFraction > 0
                        ? _options.ZScoreRetreatExitFraction
                        : 0.40;
                    if (currentProgress <= bestMeanReversionProgress * retreatFraction)
                    {
                        exit = true;
                        exitReason = "AlphaRetreat";
                    }
                }

                // [B3] OU-optimal stopping: exit when remaining z-distance no longer
                // covers cost (NegativeRemainingEdge), or when held past 2× half-life
                // (TimeBudgetExceeded). Falls through cleanly to AlphaRetreat / MaxHoldTime
                // for mean-reverting trades that don't trigger these analytic conditions.
                if (!exit && _options.UseOuOptimalStopping)
                {
                    int barsHeldOu = i - entryBar;
                    double refPriceOu = (double)a.Close;
                    double costFractionOu = (double)_options.EstimatedRoundTripCostRate;
                    var ouDecision = OuOptimalStopping.Evaluate(
                        barsHeld: barsHeldOu,
                        currentZScore: zScore,
                        targetAbsZ: exitZ,
                        halfLifeBars: currentCoint.HalfLife,
                        spreadStdDev: (double)currentCoint.SpreadStdDev,
                        referencePrice: refPriceOu,
                        roundTripCostFraction: costFractionOu,
                        timeBudgetMultiplier: _options.OuTimeBudgetMultiplier > 0
                            ? _options.OuTimeBudgetMultiplier
                            : 2.0);
                    if (ouDecision.ShouldExit)
                    {
                        exit = true;
                        exitReason = ouDecision.Reason;
                    }
                }

                if (exit)
                {
                    // Compute per-leg PnL. LongSpread = Long A, Short B. ShortSpread = Short A, Long B.
                    decimal pnlA, pnlB;
                    if (position == PairPositionState.LongSpread)
                    {
                        pnlA = (a.Close - entryPriceA) * qtyA;
                        pnlB = (entryPriceB - b.Close) * qtyB;
                    }
                    else
                    {
                        pnlA = (entryPriceA - a.Close) * qtyA;
                        pnlB = (b.Close - entryPriceB) * qtyB;
                    }

                    decimal feesA = (entryPriceA * qtyA + a.Close * qtyA) * feeRatePerLeg;
                    decimal feesB = (entryPriceB * qtyB + b.Close * qtyB) * feeRatePerLeg;
                    decimal totalFees = feesA + feesB;
                    decimal netPnl = pnlA + pnlB - totalFees;

                    equity += netPnl;
                    peakEquity = Math.Max(peakEquity, equity);
                    decimal dd = peakEquity - equity;
                    if (dd > maxDrawdown) maxDrawdown = dd;

                    decimal notional = entryPriceA * qtyA + entryPriceB * qtyB;
                    decimal pnlPct = notional > 0 ? (netPnl / notional) * 100m : 0m;

                    trades.Add(new BacktestTradeResult
                    {
                        EntryTime = aligned[entryBar].Ts,
                        ExitTime = ts,
                        Side = position == PairPositionState.LongSpread ? TradeSide.Buy : TradeSide.Sell,
                        StrategyTag = "PairsTrading",
                        RegimeTag = "PairsMeanReversion",
                        ExitPolicyTag = exitReason,
                        ExecutionMode = "SIMULATED",
                        PromotionStage = "PairsBacktest",
                        EntryPrice = entryPriceA,
                        ExitPrice = a.Close,
                        PnL = netPnl,
                        PnLPercent = pnlPct,
                        Confidence = 95,
                        ExitReason = exitReason,
                        Reasoning = $"{_options.SymbolA}/{_options.SymbolB} EntryZ={entryZScore:F2} ExitZ={zScore:F2} HL={currentCoint.HalfLife:F1} Beta={currentCoint.HedgeRatio:F3} Corr={currentCoint.Correlation:F2}",
                        TotalFeesPaid = totalFees,
                        NetFunding = 0m
                    });

                    equityCurve.Add(equity);
                    position = PairPositionState.Flat;
                    entryBar = -1;
                    bestMeanReversionProgress = 0.0;
                    lastSignalBar = i;
                    previousZScore = zScore;
                    continue;
                }
            }
            else
            {
                bool isLastBar = i == aligned.Count - 1;
                // Cooldown gate operates on `lastSignalBar`, which is set by the runner's
                // INTERNAL simulation when sim trades close. For BACKTEST integrity this is
                // correct — successive sim entries respect a cooling-off window. But for the
                // LIVE signal at the latest bar, when the hybrid executor passes
                // liveIsFlat=true (real-life flat), the cooldown is meaningless: the sim's
                // last-trade timestamp is synthetic, the operator has no real position, and
                // gating live entry on it silently suppresses signals for ~cooldown bars
                // every time the sim runs through a recent close. Diagnosed 2026-05-02:
                // BCH-LTC + TRX-XLM pairs reported "cooldown active (20-24 bars left)" via
                // skip-reason logging despite real-life open=0 — the entire reason no Pairs
                // entries fired post-prune. Bypass only on the live-decision bar; backtest
                // statistics (trade list, equity, Sharpe) are unaffected because all earlier
                // bars still respect cooldown.
                bool cooldownActive = (i - lastSignalBar) < cooldown;
                bool bypassCooldownForLiveSignal = isLastBar && liveIsFlat;
                if (cooldownActive && !bypassCooldownForLiveSignal)
                {
                    if (isLastBar) liveSkipReason = $"cooldown active ({cooldown - (i - lastSignalBar)} bars left)";
                    equityCurve.Add(equity); continue;
                }
                if (currentCoint.HalfLife > _options.MaxHalfLifeBars || currentCoint.HalfLife < _options.MinHalfLifeBars || double.IsInfinity(currentCoint.HalfLife) || double.IsNaN(currentCoint.HalfLife))
                {
                    if (isLastBar) liveSkipReason = $"half-life={currentCoint.HalfLife:F1} out of [{_options.MinHalfLifeBars},{_options.MaxHalfLifeBars}]";
                    equityCurve.Add(equity); continue;
                }

                bool wantShort = zScore > entryZ;
                bool wantLong = zScore < -entryZ;
                if (!wantShort && !wantLong && isLastBar)
                {
                    liveSkipReason = $"|z|={Math.Abs(zScore):F2} below entry {entryZ:F2}";
                }
                if (wantShort || wantLong)
                {
                    hedgeRatio = currentCoint.HedgeRatio;
                    var absBeta = Math.Abs(hedgeRatio);
                    if (absBeta < _options.MinHedgeRatio || absBeta > _options.MaxHedgeRatio)
                    {
                        if (isLastBar) liveSkipReason = $"|β|={absBeta:F3} out of [{_options.MinHedgeRatio},{_options.MaxHedgeRatio}]";
                        equityCurve.Add(equity);
                        continue;
                    }

                    // [A2] Spread velocity filter — reject when spread is still
                    // accelerating in the divergence direction. Prefer exhaustion /
                    // early reversion over catching a falling knife.
                    double velocityThreshold = _options.SpreadVelocityRejectThreshold > 0
                        ? _options.SpreadVelocityRejectThreshold : 0.35;
                    if (previousZScore.HasValue)
                    {
                        if (wantShort && zScore > previousZScore.Value + velocityThreshold)
                        {
                            if (isLastBar) liveSkipReason = $"velocity filter (short): zPrev={previousZScore.Value:F2} → zNow={zScore:F2} (Δ={zScore - previousZScore.Value:F2} > {velocityThreshold:F2}) — spread still diverging";
                            equityCurve.Add(equity);
                            previousZScore = zScore;
                            continue;
                        }
                        if (wantLong && zScore < previousZScore.Value - velocityThreshold)
                        {
                            if (isLastBar) liveSkipReason = $"velocity filter (long): zPrev={previousZScore.Value:F2} → zNow={zScore:F2} (Δ={zScore - previousZScore.Value:F2} < -{velocityThreshold:F2}) — spread still diverging";
                            equityCurve.Add(equity);
                            previousZScore = zScore;
                            continue;
                        }
                    }

                    // [A1/F1] Regime gate — classify the SPREAD's regime, not leg A's.
                    // For cointegrated pairs, mean reversion lives in the residual (Y - β·X),
                    // not in either leg. Legs can be Volatile/Trending sectorally while the
                    // spread is perfectly Ranging. Using leg-A here was a dimensional bug that
                    // killed entries on TRX/XLM, LINK/UNI etc. We compute the rolling spread
                    // log-returns and classify with ClassifyFromReturns (no ATR/ADX path).
                    // Fail-open on Unknown (insufficient data) to match warmup behavior.
                    if (effectiveRequireRanging &&
                        (i + 1) >= _options.RegimeDetectionMinBars)
                    {
                        int rWin = Math.Min(_options.RegimeDetectionMinBars, i + 1);
                        var spreadReturns = new List<double>(rWin - 1);
                        double prevSpread = 0.0;
                        bool prevSpreadValid = false;
                        for (int k = i - rWin + 1; k <= i; k++)
                        {
                            var row = aligned[k];
                            double sp = (double)(row.A.Close - currentCoint.HedgeRatio * row.B.Close);
                            if (prevSpreadValid)
                            {
                                // Arithmetic delta (spread can be negative — log not defined).
                                spreadReturns.Add(sp - prevSpread);
                            }
                            prevSpread = sp;
                            prevSpreadValid = true;
                        }
                        var regimeResult = MarketRegimeDetector.ClassifyFromReturns(
                            spreadReturns, lookback: 20);
                        if (regimeResult.Regime != MarketRegime.Ranging &&
                            regimeResult.Regime != MarketRegime.Unknown)
                        {
                            if (isLastBar) liveSkipReason = $"spread regime={regimeResult.Regime} (need Ranging)";
                            equityCurve.Add(equity);
                            previousZScore = zScore;
                            continue;
                        }
                    }

                    // [W5] StopHuntDetector defensive entry guard — skip entry when leg A
                    // is actively in a stop-hunt pattern (wick swept swing extreme + reversal close).
                    // We're trying to enter a mean-reversion trade; entering DURING the hunt
                    // means buying the wick instead of the reversal.
                    if (_options.BlockEntryOnStopHunt)
                    {
                        int huntWin = StopHuntDetector.DefaultLookbackBars + StopHuntDetector.DefaultAtrPeriod + 5;
                        if ((i + 1) >= huntWin)
                        {
                            var huntCandles = new List<Candle>(huntWin);
                            for (int k = i - huntWin + 1; k <= i; k++) huntCandles.Add(aligned[k].A);
                            var hunt = StopHuntDetector.DetectOnLatestBar(huntCandles);
                            if (hunt is not null && hunt.StrengthScore >= 50)
                            {
                                if (isLastBar) liveSkipReason = $"stop-hunt active on leg-A (strength={hunt.StrengthScore})";
                                equityCurve.Add(equity);
                                previousZScore = zScore;
                                continue;
                            }
                        }
                    }

                    // [W4] Pair-quality composite gate — opt-in via MinPairQualityScore.
                    // Filters entries on pairs that JUST passed cointegration but have
                    // low Hurst/correlation/ADF strength (marginal admissions). When set
                    // to 0 (default) this is a no-op; production paper recommendation is 50.
                    if (_options.MinPairQualityScore > 0)
                    {
                        int qualityScore = ComputePairQualityScore(currentCoint);
                        if (qualityScore < _options.MinPairQualityScore)
                        {
                            if (isLastBar) liveSkipReason = $"pair quality score={qualityScore} < {_options.MinPairQualityScore}";
                            equityCurve.Add(equity);
                            previousZScore = zScore;
                            continue;
                        }
                    }

                    // [A3/F3] Confluence floors — VOLUME ONLY on leg A (liquidity check).
                    // Vol-floor was REMOVED: it used price ATR which is the wrong dimension
                    // for pairs (price vol ≠ spread vol). Spread-σ floor is handled below
                    // via MinSpreadAtrBps using the cointegration-state's SpreadStdDev.
                    if (_options.ApplyConfluenceFloors)
                    {
                        int cWinNeeded = EntryConfluenceService.EmaTrendLookback +
                                         EntryConfluenceService.EmaSlopeBars + 5;
                        if ((i + 1) >= cWinNeeded)
                        {
                            int cWin = Math.Min(cWinNeeded, i + 1);
                            var confluenceWindow = new List<Candle>(cWin);
                            for (int k = i - cWin + 1; k <= i; k++) confluenceWindow.Add(aligned[k].A);
                            var sideTag = wantShort ? TradeSide.Sell : TradeSide.Buy;
                            var gateResult = EntryConfluenceService.Evaluate(
                                confluenceWindow, sideTag, MarketRegime.Ranging,
                                enableTrendAlignment: false,
                                enableVolatilityFloor: false,
                                enableVolumeFloor: true);
                            if (!gateResult.Pass)
                            {
                                if (isLastBar) liveSkipReason = $"confluence floor reject: {gateResult.RejectReason}";
                                equityCurve.Add(equity);
                                previousZScore = zScore;
                                continue;
                            }
                        }
                    }

                    // [F3] Spread-aware volatility floor: σ_spread / refPrice in basis points.
                    // A pair whose residual barely moves can't generate enough alpha to clear
                    // round-trip cost — skip these entries. 0 disables the gate (default).
                    if (_options.MinSpreadAtrBps > 0 && currentCoint.SpreadStdDev > 0m && a.Close > 0m)
                    {
                        double spreadBps = (double)(currentCoint.SpreadStdDev / a.Close) * 10_000.0;
                        if (spreadBps < _options.MinSpreadAtrBps)
                        {
                            if (isLastBar) liveSkipReason = $"spread σ={spreadBps:F1}bps < floor {_options.MinSpreadAtrBps}bps (cost won't be earned)";
                            equityCurve.Add(equity);
                            previousZScore = zScore;
                            continue;
                        }
                    }

                    double hlMult = currentCoint.HalfLife <= 10 ? 1.0 : Math.Max(0.3, 1.0 - (currentCoint.HalfLife - 10.0) / 150.0);
                    decimal drawdownScale = peakEquity > 0m
                        ? Math.Clamp(equity / peakEquity, _options.DrawdownExposureScaleFloor, 1.0m)
                        : 1.0m;
                    decimal corrScale = Math.Clamp((decimal)((currentCoint.Correlation - _options.MinCorrelation) / Math.Max(0.05, 1.0 - _options.MinCorrelation)), 0.25m, 1.0m);
                    // Leverage with hard cap + DD-based throttle (2026-04-24).
                    var lev = Math.Min(leverageOverride ?? _options.Leverage, _options.MaxLeverage);
                    if (_options.DrawdownThrottleFraction > 0m && _options.MaxDrawdownBudget > 0m)
                    {
                        decimal realizedDd = peakEquity > 0m ? (peakEquity - equity) / peakEquity : 0m;
                        if (realizedDd >= _options.DrawdownThrottleFraction * _options.MaxDrawdownBudget)
                            lev *= 0.5m;
                    }
                    // [W6] GARCH(1,1) forward-vol-aware sizing scalar.
                    // When the model predicts forward vol > recent realized, downsize.
                    // When predicted vol < recent, upsize (within bounds).
                    decimal garchScale = 1.0m;
                    if (_options.UseGarchForwardVolSizing)
                    {
                        int gWin = Math.Max(30, _options.GarchFitLookbackBars);
                        if ((i + 1) >= gWin + 1)
                        {
                            var legAReturns = new double[gWin];
                            for (int k = 0; k < gWin; k++)
                            {
                                var prev = (double)aligned[i - gWin + k].A.Close;
                                var cur = (double)aligned[i - gWin + k + 1].A.Close;
                                legAReturns[k] = prev > 0 && cur > 0 ? Math.Log(cur / prev) : 0.0;
                            }
                            try
                            {
                                var fit = Garch11Volatility.Fit(legAReturns, gridSteps: 6);
                                var sigmas = Garch11Volatility.ConditionalVariances(legAReturns, fit);
                                double recentVar = sigmas[^1];
                                double forwardVar = Garch11Volatility.ForecastNextVariance(
                                    legAReturns[^1], recentVar, fit);
                                if (recentVar > 0 && forwardVar > 0)
                                {
                                    // Inverse-vol scaling: high forward vol → smaller size.
                                    double ratio = Math.Sqrt(recentVar / forwardVar);
                                    garchScale = (decimal)Math.Clamp(ratio, 0.4, 1.6);
                                }
                            }
                            catch { /* fit failed — fall back to scale 1.0 */ }
                        }
                    }
                    decimal targetGrossExposure = Math.Max(
                        250m,
                        equity * _options.MaxGrossExposurePercent * (decimal)hlMult * drawdownScale * corrScale * lev * garchScale);

                    entryPriceA = a.Close;
                    entryPriceB = b.Close;
                    decimal grossDenominator = Math.Max(0.0000001m, entryPriceA + (absBeta * entryPriceB));
                    decimal baseUnits = targetGrossExposure / grossDenominator;

                    qtyA = baseUnits;
                    qtyB = absBeta * baseUnits;

                    position = wantShort ? PairPositionState.ShortSpread : PairPositionState.LongSpread;
                    entryBar = i;
                    entryZScore = zScore;
                    bestMeanReversionProgress = 0.0;
                    lastSignalBar = i;
                }
            }

            previousZScore = zScore;
            equityCurve.Add(equity);
        }

        // Capture the runner's "live" decision on the LATEST bar — used by the hybrid pairs
        // executor to decide whether to place real OKX demo orders RIGHT NOW.
        int latestBarIdx = aligned.Count - 1;
        PairsLiveSignal liveSignal;
        if (latestBarIdx < 0 || coint is null)
        {
            liveSignal = PairsLiveSignal.None;
        }
        else
        {
            var lastTs = aligned[latestBarIdx].Ts;
            var lastA = aligned[latestBarIdx].A;
            var lastB = aligned[latestBarIdx].B;

            // When called from hybrid with liveIsFlat=true, we treat backtest's internal position
            // as if flat — so a fresh WouldOpenNew can fire even if the simulation entered earlier
            // and is "still holding". Same fix pattern as FundingAlphaBacktestRunner.
            var effectivePosition = liveIsFlat ? PairPositionState.Flat : position;
            if (effectivePosition != PairPositionState.Flat)
            {
                var sideTag = effectivePosition == PairPositionState.LongSpread ? PairsLiveSide.LongSpread : PairsLiveSide.ShortSpread;
                if (entryBar == latestBarIdx)
                {
                    liveSignal = new PairsLiveSignal(
                        PairsLiveSignalKind.WouldOpenNew, sideTag,
                        lastA.Close, lastB.Close, qtyA, qtyB, hedgeRatio, entryZScore,
                        $"Spread Z={entryZScore:F2} entered on latest bar (β={hedgeRatio:F3} HL={coint.Value.HalfLife:F1})");
                }
                else
                {
                    liveSignal = new PairsLiveSignal(
                        PairsLiveSignalKind.HoldOpen, sideTag,
                        entryPriceA, entryPriceB, qtyA, qtyB, hedgeRatio, entryZScore,
                        $"Holding spread Z entry={entryZScore:F2} bar={entryBar}/{latestBarIdx}");
                }
            }
            else
            {
                var lastTrade = trades.LastOrDefault();
                if (lastTrade is not null && lastTrade.ExitTime == lastTs)
                {
                    var oppSide = lastTrade.Side == TradeSide.Buy ? PairsLiveSide.LongSpread : PairsLiveSide.ShortSpread;
                    liveSignal = new PairsLiveSignal(
                        PairsLiveSignalKind.WouldClose, oppSide,
                        lastA.Close, lastB.Close, qtyA, qtyB, hedgeRatio, 0,
                        $"Exit on latest bar: {lastTrade.ExitReason}");
                }
                else
                {
                    liveSignal = liveSkipReason is null
                        ? PairsLiveSignal.None
                        : PairsLiveSignal.None with { Reasoning = liveSkipReason };
                }
            }
        }

        LastLiveSignal = liveSignal;

        int winners = trades.Count(t => t.PnL > 0);
        int losers = trades.Count(t => t.PnL <= 0);
        decimal grossProfit = trades.Where(t => t.PnL > 0).Sum(t => t.PnL);
        decimal grossLoss = Math.Abs(trades.Where(t => t.PnL < 0).Sum(t => t.PnL));
        double profitFactor = grossLoss > 0 ? (double)(grossProfit / grossLoss) : (grossProfit > 0 ? double.PositiveInfinity : 0.0);

        var returns = new List<double>();
        for (int i = 1; i < equityCurve.Count; i++)
        {
            decimal prev = equityCurve[i - 1];
            if (prev <= 0) continue;
            returns.Add((double)((equityCurve[i] - prev) / prev));
        }
        double sharpe = ComputeSharpe(returns);
        double sortino = ComputeSortino(returns);
        double avgRr = trades.Count > 0 && losers > 0
            ? (double)(grossProfit / Math.Max(1, winners)) / Math.Max(1e-9, (double)(grossLoss / Math.Max(1, losers)))
            : 0.0;

        decimal totalPnl = equity - startingCapital;
        decimal ddPct = startingCapital > 0 ? (maxDrawdown / startingCapital) * 100m : 0m;
        decimal totalFeesPaid = trades.Sum(t => t.TotalFeesPaid);
        decimal grossPnl = totalPnl + totalFeesPaid;
        double costLeakage = grossPnl > 0 ? (double)(totalFeesPaid / grossPnl) * 100.0 : 0.0;

        _logger.LogInformation(
            "Pairs backtest {A}/{B} done. Trades={Trades} WR={WR:F1}% PnL={PnL:F2} Sharpe={Sharpe:F2} DD={DD:F2}%",
            _options.SymbolA, _options.SymbolB, trades.Count,
            trades.Count > 0 ? winners * 100.0 / trades.Count : 0.0,
            totalPnl, sharpe, ddPct);
        _logger.LogInformation(
            "Pairs diagnostics: alignedBars={Aligned} cointTests={CT} cointPassed={CP} zEvaluated={ZE} |Zmax|={ZMax:F2} halfLife={HL}",
            aligned.Count, cointTestCount, cointPassCount, signalArmed, absMaxZ,
            coint?.HalfLife.ToString("F1") ?? "n/a");

        // Shadow threshold report: "of N bars where coint admitted, how many would have crossed
        // entry threshold X?" — gives operator real edge-supply visibility independent of live
        // gate. If shadow@2.5 == 0 but shadow@1.5 is also small, regime is genuinely tight.
        // If shadow@2.5 is small but shadow@1.5 is large, threshold tuning is the bottleneck.
        // Only emit when sample is meaningful (≥30 valid-z bars).
        if (barsWithValidZ >= 30)
        {
            _logger.LogInformation(
                "Pairs shadow {A}/{B}: validZ={N} | crossings: σ≥1.0→{S10} ({P10:F1}%) σ≥1.5→{S15} ({P15:F1}%) σ≥2.0→{S20} ({P20:F1}%) σ≥2.5→{S25} ({P25:F1}%) σ≥3.0→{S30} ({P30:F1}%)",
                _options.SymbolA, _options.SymbolB, barsWithValidZ,
                shadowAbove1_0, 100.0 * shadowAbove1_0 / barsWithValidZ,
                shadowAbove1_5, 100.0 * shadowAbove1_5 / barsWithValidZ,
                shadowAbove2_0, 100.0 * shadowAbove2_0 / barsWithValidZ,
                shadowAbove2_5, 100.0 * shadowAbove2_5 / barsWithValidZ,
                shadowAbove3_0, 100.0 * shadowAbove3_0 / barsWithValidZ);
        }

        return new BacktestResult
        {
            Symbol = $"{_options.SymbolA}-{_options.SymbolB}",
            TotalTrades = trades.Count,
            WinningTrades = winners,
            LosingTrades = losers,
            TotalPnL = totalPnl,
            MaxDrawdown = maxDrawdown,
            MaxDrawdownPercent = ddPct,
            SharpeRatio = sharpe,
            SortinoRatio = sortino,
            AverageRR = avgRr,
            ProfitFactor = profitFactor,
            MaxConsecutiveLosses = MaxConsecutiveLosses(trades),
            StartingCapital = startingCapital,
            FinalEquity = equity,
            EquityCurve = equityCurve,
            Trades = trades,
            StartDate = aligned.Count > 0 ? aligned[0].Ts : DateTime.UtcNow,
            EndDate = aligned.Count > 0 ? aligned[^1].Ts : DateTime.UtcNow,
            Reasoning = $"Pairs backtest {_options.SymbolA}/{_options.SymbolB} lookback={lookback} zWin={zWindow} corr>={_options.MinCorrelation:F2}",
            TotalFeesPaid = totalFeesPaid,
            GrossPnL = grossPnl,
            CostLeakagePercent = costLeakage
        };
    }

    /// <summary>
    /// Permissive tradeability gate: strict ADF gamma thresholds (e.g. -0.25) rarely fire
    /// for crypto pairs at intraday timeframes. Instead, accept the pair when the residual
    /// has a finite, positive half-life within a sensible bound and a usable std-dev.
    /// </summary>
    private bool IsTradeable(PairSignalState r)
    {
        if (r.SpreadStdDev <= 0) return false;
        if (double.IsInfinity(r.HalfLife) || double.IsNaN(r.HalfLife)) return false;
        if (r.HalfLife < _options.MinHalfLifeBars || r.HalfLife > _options.MaxHalfLifeBars) return false;
        if (r.AdfTestStatistic >= 0m) return false;
        var absBeta = Math.Abs(r.HedgeRatio);
        if (absBeta < _options.MinHedgeRatio || absBeta > _options.MaxHedgeRatio) return false;
        if (r.Correlation < _options.MinCorrelation) return false;
        return true;
    }

    /// <summary>
    /// Composite pair-quality score 0-100 used by the [W4] entry-quality gate.
    /// Rewards pairs that not only pass cointegration but pass it convincingly.
    /// Note: Hurst is part of EngleGrangerDetector.CointegrationResult but is not
    /// retained on PairSignalState — using only the fields available here.
    ///   • |Correlation| (max 40): 0.80 = 40, 0.65 = 28, 0.55 = 18, 0.45 = 8.
    ///   • |ADF t-stat| (max 35): -3.5 → 35, -2.5 → 24, -2.0 → 16, -1.5 → 8.
    ///   • Half-life sweet spot (max 25): 5-30 bars = 25, 3-50 = 15, 2-80 = 7.
    /// </summary>
    private static int ComputePairQualityScore(PairSignalState r)
    {
        double absCorr = Math.Abs(r.Correlation);
        int corr = absCorr >= 0.80 ? 40 : absCorr >= 0.65 ? 28 : absCorr >= 0.55 ? 18 : absCorr >= 0.45 ? 8 : 0;
        double adfT = (double)r.AdfTestStatistic;
        int adf = adfT <= -3.5 ? 35 : adfT <= -2.5 ? 24 : adfT <= -2.0 ? 16 : adfT <= -1.5 ? 8 : 0;
        int hl = r.HalfLife switch
        {
            >= 5.0 and <= 30.0 => 25,
            >= 3.0 and <= 50.0 => 15,
            >= 2.0 and <= 80.0 => 7,
            _ => 0
        };
        return Math.Clamp(corr + adf + hl, 0, 100);
    }

    private static double ComputeCorrelation(IReadOnlyList<decimal> y, IReadOnlyList<decimal> x)
    {
        if (y.Count != x.Count || y.Count < 2) return 0.0;
        double meanY = y.Average(v => (double)v);
        double meanX = x.Average(v => (double)v);
        double cov = 0.0, varY = 0.0, varX = 0.0;
        for (int i = 0; i < y.Count; i++)
        {
            double dy = (double)y[i] - meanY;
            double dx = (double)x[i] - meanX;
            cov += dy * dx;
            varY += dy * dy;
            varX += dx * dx;
        }

        double denom = Math.Sqrt(varY * varX);
        return denom <= 0 ? 0.0 : cov / denom;
    }

    private static List<(DateTime Ts, Candle A, Candle B)> AlignByTimestamp(
        IReadOnlyList<Candle> candlesA,
        IReadOnlyList<Candle> candlesB)
    {
        var mapB = new Dictionary<DateTime, Candle>(candlesB.Count);
        foreach (var c in candlesB) mapB[c.Timestamp] = c;

        var aligned = new List<(DateTime, Candle, Candle)>(candlesA.Count);
        foreach (var a in candlesA)
        {
            if (mapB.TryGetValue(a.Timestamp, out var b))
            {
                if (a.Close > 0 && b.Close > 0)
                    aligned.Add((a.Timestamp, a, b));
            }
        }
        aligned.Sort((l, r) => l.Item1.CompareTo(r.Item1));
        return aligned;
    }

    private static double ComputeSharpe(List<double> returns)
    {
        if (returns.Count < 2) return 0.0;
        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        double std = Math.Sqrt(variance);
        if (std <= 0) return 0.0;
        return (mean / std) * Math.Sqrt(252.0);
    }

    private static double ComputeSortino(List<double> returns)
    {
        if (returns.Count < 2) return 0.0;
        double mean = returns.Average();
        var downside = returns.Where(r => r < 0).ToList();
        if (downside.Count == 0) return double.PositiveInfinity;
        double dVar = downside.Sum(r => r * r) / downside.Count;
        double dStd = Math.Sqrt(dVar);
        if (dStd <= 0) return 0.0;
        return (mean / dStd) * Math.Sqrt(252.0);
    }

    private static int MaxConsecutiveLosses(List<BacktestTradeResult> trades)
    {
        int max = 0, current = 0;
        foreach (var t in trades)
        {
            if (t.PnL <= 0) { current++; if (current > max) max = current; }
            else current = 0;
        }
        return max;
    }

    private static BacktestResult EmptyResult(decimal capital, List<(DateTime Ts, Candle A, Candle B)> aligned, string reason)
        => new()
        {
            Symbol = "PAIRS",
            StartingCapital = capital,
            FinalEquity = capital,
            EquityCurve = new List<decimal> { capital },
            Reasoning = reason,
            StartDate = aligned.Count > 0 ? aligned[0].Ts : DateTime.UtcNow,
            EndDate = aligned.Count > 0 ? aligned[^1].Ts : DateTime.UtcNow
        };

    private readonly record struct PairSignalState(
        decimal HedgeRatio,
        decimal SpreadAverage,
        decimal SpreadStdDev,
        decimal AdfTestStatistic,
        double HalfLife,
        double Correlation)
    {
        public PairSignalState(EngleGrangerDetector.CointegrationResult result, double correlation)
            : this(result.HedgeRatio, result.SpreadAverage, result.SpreadStdDev, result.AdfTestStatistic, result.HalfLife, correlation)
        {
        }
    }
}

public enum PairsLiveSignalKind { None, HoldOpen, WouldOpenNew, WouldClose }
public enum PairsLiveSide { LongSpread, ShortSpread }

public sealed record PairsLiveSignal(
    PairsLiveSignalKind Kind,
    PairsLiveSide Side,
    decimal PriceA,        // ref price of leg Y at decision moment
    decimal PriceB,        // ref price of leg X at decision moment
    decimal QtyA,          // sized qty for leg Y
    decimal QtyB,          // sized qty for leg X (= |β|·QtyA roughly)
    decimal HedgeRatio,    // β (signed)
    double EntryZScore,    // z-score at entry (0 for close/none)
    string Reasoning)
{
    public static PairsLiveSignal None => new(
        PairsLiveSignalKind.None, PairsLiveSide.LongSpread,
        0m, 0m, 0m, 0m, 0m, 0, "no signal");
}
