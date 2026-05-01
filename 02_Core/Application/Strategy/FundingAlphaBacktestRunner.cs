using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Funding-rate as STANDALONE alpha — fades crowded perpetual positioning.
///
/// Hypothesis (documented in crypto literature, 2021+): persistent high positive
/// funding rate means longs are paying shorts at elevated rates, indicating crowded
/// long positioning. This structurally precedes mean-reversion / local tops because
/// (a) funding is a real cost eroding longs' equity, and (b) crowded positioning is
/// fragile to macro shocks. Symmetric logic for negative funding.
///
/// Entry:
///   funding ≥ +<see cref="FundingAlphaConfig.EntryThreshold"/> → SHORT
///   funding ≤ -<see cref="FundingAlphaConfig.EntryThreshold"/> → LONG
///
/// Exit (first-hit wins):
///   (a) funding normalizes: |funding| &lt; <see cref="FundingAlphaConfig.ExitThreshold"/>
///   (b) take profit at ±<see cref="FundingAlphaConfig.TakeProfitPct"/> price move in our favor
///   (c) stop loss at ±<see cref="FundingAlphaConfig.StopLossPct"/> adverse price move
///   (d) max holding bars (default 16h = 2 funding cycles)
///
/// Position sizing: base size × funding-strength multiplier (stronger funding = more conviction).
/// </summary>
public sealed class FundingAlphaBacktestRunner
{
    private readonly ILogger<FundingAlphaBacktestRunner> _logger;
    private readonly IFundingRateProvider _fundingProvider;

    public FundingAlphaBacktestRunner(
        ILogger<FundingAlphaBacktestRunner> logger,
        IFundingRateProvider fundingProvider)
    {
        _logger = logger;
        _fundingProvider = fundingProvider;
    }

    public async Task<FundingAlphaResult> RunAsync(
        string symbol,
        IReadOnlyList<Candle> candles,
        FundingAlphaConfig config,
        decimal startingCapital,
        CancellationToken ct = default,
        bool liveIsFlat = false)
    {
        if (candles.Count < 60)
            return FundingAlphaResult.Empty(symbol, "insufficient candles");

        // Prefetch funding history for the backtest window.
        try
        {
            await _fundingProvider.PrefetchAsync(
                new[] { symbol }, candles[0].Timestamp.AddDays(-2), ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Funding prefetch failed for {Symbol}", symbol); }

        var trades = new List<FundingAlphaTrade>();
        decimal equity = startingCapital;
        decimal peakEquity = startingCapital;
        decimal maxDrawdown = 0m;

        // Resolve effective thresholds once (per-symbol override or default fallback).
        // Backtest and live executor share the same helper so behavior cannot drift.
        var effEntry = GetEffectiveEntryThreshold(symbol, config);
        var effExit  = GetEffectiveExitThreshold(symbol, config);

        OpenPosition? open = null;
        int fundingObservations = 0;
        int entrySignals = 0;
        decimal sumFunding = 0m;
        decimal maxAbsFunding = 0m;

        for (int i = 0; i < candles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var bar = candles[i];
            var funding = _fundingProvider.GetRate(symbol, bar.Timestamp);
            if (funding != 0m)
            {
                fundingObservations++;
                sumFunding += funding;
                var absF = Math.Abs(funding);
                if (absF > maxAbsFunding) maxAbsFunding = absF;
            }

            // --- Exit logic (if in position) ---
            if (open is not null)
            {
                bool exit = false;
                string exitReason = "";
                decimal exitPrice = bar.Close;

                // (a) Funding normalized (thesis played out).
                if (funding != 0m && Math.Abs(funding) < effExit)
                { exit = true; exitReason = "FundingNormalized"; }

                // (b) Take profit
                if (!exit)
                {
                    var movePct = open.Side == TradeSide.Buy
                        ? (bar.Close - open.EntryPrice) / open.EntryPrice
                        : (open.EntryPrice - bar.Close) / open.EntryPrice;
                    if (movePct >= config.TakeProfitPct)
                    {
                        exit = true; exitReason = "TakeProfit";
                        exitPrice = open.Side == TradeSide.Buy
                            ? open.EntryPrice * (1 + config.TakeProfitPct)
                            : open.EntryPrice * (1 - config.TakeProfitPct);
                    }
                }

                // (c) Stop loss
                if (!exit)
                {
                    var adversePct = open.Side == TradeSide.Buy
                        ? (open.EntryPrice - bar.Low) / open.EntryPrice
                        : (bar.High - open.EntryPrice) / open.EntryPrice;
                    if (adversePct >= config.StopLossPct)
                    {
                        exit = true; exitReason = "StopLoss";
                        exitPrice = open.Side == TradeSide.Buy
                            ? open.EntryPrice * (1 - config.StopLossPct)
                            : open.EntryPrice * (1 + config.StopLossPct);
                    }
                }

                // (d) Max hold time
                if (!exit && (i - open.EntryBar) >= config.MaxHoldingBars)
                { exit = true; exitReason = "TimeStop"; }

                if (exit)
                {
                    var gross = open.Side == TradeSide.Buy
                        ? (exitPrice - open.EntryPrice) * open.Qty
                        : (open.EntryPrice - exitPrice) * open.Qty;
                    // Round-trip cost approximation: taker × 2 + slippage.
                    var notional = exitPrice * open.Qty;
                    var costs = notional * config.RoundTripCostRate;
                    var pnl = gross - costs;

                    equity += pnl;
                    if (equity > peakEquity) peakEquity = equity;
                    var dd = peakEquity > 0 ? (peakEquity - equity) / peakEquity : 0m;
                    if (dd > maxDrawdown) maxDrawdown = dd;

                    trades.Add(new FundingAlphaTrade(
                        open.Side, open.EntryTimeUtc, bar.Timestamp,
                        open.EntryPrice, exitPrice, open.Qty, pnl, gross, costs,
                        open.FundingAtEntry, funding, exitReason));

                    open = null;
                }
            }

            // --- Entry logic (if flat) ---
            if (open is null && funding != 0m && !IsSettlementBar(bar.Timestamp, config))
            {
                TradeSide? side = null;
                bool fromFlip = false;
                decimal sizeFractionBase;

                if (funding >= effEntry)
                {
                    side = TradeSide.Sell;
                    sizeFractionBase = config.BaseSizeFraction;
                }
                else if (funding <= -effEntry)
                {
                    side = TradeSide.Buy;
                    sizeFractionBase = config.BaseSizeFraction;
                }
                else if (config.UseFlipCatalyst)
                {
                    // [W1] Funding flip catalyst: even when current funding is below
                    // the standard threshold, a RECENT sign flip is itself an entry signal.
                    // Pulls the recent funding history (up to 16 bars back ≈ 2 funding cycles
                    // on a 1h candle frame) and looks for an unconsumed sign change.
                    int historyLookback = Math.Min(i, 16);
                    if (historyLookback >= 2)
                    {
                        var historySlice = new List<(DateTime Timestamp, decimal Rate)>(historyLookback);
                        for (int k = i - historyLookback; k <= i; k++)
                        {
                            var rk = _fundingProvider.GetRate(symbol, candles[k].Timestamp);
                            historySlice.Add((candles[k].Timestamp, rk));
                        }
                        var maxAge = TimeSpan.FromHours(config.FlipMaxAgeBars);
                        var flip = FundingFlipDetector.DetectLatestFlip(
                            historySlice, symbol,
                            minRateMagnitudeBps: config.FlipMinMagnitudeBps,
                            maxAgeSinceFlip: maxAge);
                        if (flip is not null)
                        {
                            side = flip.Side;
                            fromFlip = true;
                            sizeFractionBase = config.BaseSizeFraction * config.FlipSizeMultiplier;
                        }
                        else sizeFractionBase = config.BaseSizeFraction;
                    }
                    else sizeFractionBase = config.BaseSizeFraction;
                }
                else sizeFractionBase = config.BaseSizeFraction;

                if (side is not null)
                {
                    entrySignals++;
                    decimal sizeFraction;
                    if (fromFlip)
                    {
                        // Flip-only signal: use FlipSizeMultiplier-scaled base, no strength escalation.
                        sizeFraction = Math.Min(sizeFractionBase, config.MaxSizeFraction);
                    }
                    else
                    {
                        // Threshold-based: classic strength scaling.
                        var excess = (double)(Math.Abs(funding) - effEntry);
                        var rawMult = 1.0 + config.StrengthScale * excess / (double)effEntry;
                        var strengthMult = (decimal)Math.Min(rawMult, (double)config.MaxStrengthMult);
                        sizeFraction = Math.Min(
                            sizeFractionBase * strengthMult,
                            config.MaxSizeFraction);
                    }

                    // Leverage with hard cap + DD-based throttle.
                    var lev = Math.Min(config.Leverage, config.MaxLeverage);
                    if (config.DrawdownThrottleFraction > 0m && config.MaxDrawdownBudget > 0m)
                    {
                        var ddFraction = maxDrawdown / config.MaxDrawdownBudget;
                        if (ddFraction >= config.DrawdownThrottleFraction)
                            lev *= 0.5m;   // halve leverage when risk budget starts breaching
                    }
                    var notional = equity * sizeFraction * lev;
                    var qty = bar.Close > 0 ? notional / bar.Close : 0m;
                    if (qty > 0m)
                    {
                        open = new OpenPosition(
                            Side: side.Value,
                            EntryBar: i,
                            EntryTimeUtc: bar.Timestamp,
                            EntryPrice: bar.Close,
                            Qty: qty,
                            FundingAtEntry: funding);
                    }
                }
            }
        }

        // Capture the runner's "live" decision on the LATEST bar — used by the hybrid
        // executor to decide whether to place a real OKX demo order RIGHT NOW. Three states:
        //   - HoldOpen      : a position is open and exit conditions are NOT yet met
        //   - WouldOpenNew  : flat, latest bar's funding would trigger a fresh entry
        //   - WouldClose    : open and latest bar's exit conditions met
        //   - None          : flat, no entry signal
        FundingLiveSignal liveSignal;
        var lastBarLive = candles[^1];
        var lastFunding = _fundingProvider.GetRate(symbol, lastBarLive.Timestamp);
        // Hybrid call site passes liveIsFlat=true when its REAL position state is flat,
        // overriding the backtest's internal `open` (which reflects what the simulation
        // would have done, not what the live executor actually holds). Without this, when
        // the backtest opened a position on the latest bar, the runner would emit
        // HoldOpen/WouldClose and the hybrid (which has no real position) would do nothing
        // — silently swallowing the entry signal.
        var effectiveOpen = liveIsFlat ? null : open;
        if (effectiveOpen is not null)
        {
            var openPos = effectiveOpen; // null-flow narrowed
            // Re-evaluate exit conditions on latest bar.
            bool exit = false;
            string exitReason = "";
            if (lastFunding != 0m && Math.Abs(lastFunding) < effExit) { exit = true; exitReason = "FundingNormalized"; }
            if (!exit)
            {
                var movePct = openPos.Side == TradeSide.Buy
                    ? (lastBarLive.Close - openPos.EntryPrice) / openPos.EntryPrice
                    : (openPos.EntryPrice - lastBarLive.Close) / openPos.EntryPrice;
                if (movePct >= config.TakeProfitPct) { exit = true; exitReason = "TakeProfit"; }
            }
            if (!exit)
            {
                var adversePct = openPos.Side == TradeSide.Buy
                    ? (openPos.EntryPrice - lastBarLive.Low) / openPos.EntryPrice
                    : (lastBarLive.High - openPos.EntryPrice) / openPos.EntryPrice;
                if (adversePct >= config.StopLossPct) { exit = true; exitReason = "StopLoss"; }
            }
            if (!exit && (candles.Count - 1 - openPos.EntryBar) >= config.MaxHoldingBars)
            { exit = true; exitReason = "TimeStop"; }

            liveSignal = exit
                ? new FundingLiveSignal(FundingLiveSignalKind.WouldClose, openPos.Side, openPos.Qty, lastBarLive.Close, lastFunding, exitReason)
                : new FundingLiveSignal(FundingLiveSignalKind.HoldOpen, openPos.Side, openPos.Qty, openPos.EntryPrice, openPos.FundingAtEntry, "Holding");
        }
        else if (lastFunding != 0m && Math.Abs(lastFunding) >= effEntry && !IsSettlementBar(lastBarLive.Timestamp, config))
        {
            var newSide = lastFunding >= effEntry ? TradeSide.Sell : TradeSide.Buy;
            var excess = (double)(Math.Abs(lastFunding) - effEntry);
            var rawMult = 1.0 + config.StrengthScale * excess / (double)effEntry;
            var strengthMult = (decimal)Math.Min(rawMult, (double)config.MaxStrengthMult);
            var sizeFraction = Math.Min(config.BaseSizeFraction * strengthMult, config.MaxSizeFraction);
            var lev = Math.Min(config.Leverage, config.MaxLeverage);
            var notional = equity * sizeFraction * lev;
            var qty = lastBarLive.Close > 0 ? notional / lastBarLive.Close : 0m;
            liveSignal = qty > 0m
                ? new FundingLiveSignal(FundingLiveSignalKind.WouldOpenNew, newSide, qty, lastBarLive.Close, lastFunding,
                    $"Funding {lastFunding:F5} {(lastFunding >= 0 ? ">=":"<=")} threshold {effEntry:F5}")
                : FundingLiveSignal.None;
        }
        else
        {
            liveSignal = FundingLiveSignal.None;
        }

        // Force close at end (backtest accounting only — hybrid executor uses LiveSignal instead).
        if (open is not null)
        {
            var lastBar = candles[^1];
            var gross = open.Side == TradeSide.Buy
                ? (lastBar.Close - open.EntryPrice) * open.Qty
                : (open.EntryPrice - lastBar.Close) * open.Qty;
            var costs = lastBar.Close * open.Qty * config.RoundTripCostRate;
            var pnl = gross - costs;
            equity += pnl;
            trades.Add(new FundingAlphaTrade(
                open.Side, open.EntryTimeUtc, lastBar.Timestamp,
                open.EntryPrice, lastBar.Close, open.Qty, pnl, gross, costs,
                open.FundingAtEntry, _fundingProvider.GetRate(symbol, lastBar.Timestamp),
                "ForceClose"));
        }

        // Aggregate metrics.
        var wins = trades.Where(t => t.NetPnl > 0).ToList();
        var losses = trades.Where(t => t.NetPnl <= 0).ToList();
        decimal totalPnl = trades.Sum(t => t.NetPnl);
        decimal grossProfit = wins.Sum(t => t.NetPnl);
        decimal grossLoss = Math.Abs(losses.Sum(t => t.NetPnl));
        double winRate = trades.Count > 0 ? (double)wins.Count / trades.Count : 0.0;
        decimal profitFactor = grossLoss > 0m ? grossProfit / grossLoss : grossProfit > 0m ? 999m : 0m;

        double sharpe = ComputeSharpe(trades, startingCapital);
        double avgFunding = fundingObservations > 0 ? (double)(sumFunding / fundingObservations) : 0.0;

        _logger.LogInformation(
            "Funding alpha {Symbol}: trades={T} WR={WR:P1} PnL={P:N2} Sharpe={S:F2} PF={PF:F2} DD={DD:P2} | signals={Sig} fundingObs={FObs} avgFunding={AF:F5} maxAbsFunding={MF:F5}",
            symbol, trades.Count, winRate, totalPnl, sharpe, profitFactor, maxDrawdown,
            entrySignals, fundingObservations, avgFunding, maxAbsFunding);

        return new FundingAlphaResult(
            symbol,
            trades.Count, wins.Count, losses.Count,
            winRate, totalPnl, grossProfit, grossLoss, profitFactor,
            sharpe, maxDrawdown, equity - startingCapital,
            startingCapital, equity,
            fundingObservations, entrySignals, avgFunding, (double)maxAbsFunding,
            trades,
            liveSignal);
    }

    /// <summary>
    /// Returns the effective entry threshold for a symbol — per-symbol override if set,
    /// otherwise the global default. Single source of truth used by both backtest and
    /// live executor, so override / default behavior cannot drift between them.
    /// </summary>
    public static decimal GetEffectiveEntryThreshold(string symbol, FundingAlphaConfig config)
    {
        if (!string.IsNullOrEmpty(symbol)
            && config.PerSymbolEntryThreshold is { } d
            && d.TryGetValue(symbol, out var v)
            && v > 0m)
        {
            return v;
        }
        return config.EntryThreshold;
    }

    /// <summary>Same idea for the exit (funding-normalized) threshold.</summary>
    public static decimal GetEffectiveExitThreshold(string symbol, FundingAlphaConfig config)
    {
        if (!string.IsNullOrEmpty(symbol)
            && config.PerSymbolExitThreshold is { } d
            && d.TryGetValue(symbol, out var v)
            && v > 0m)
        {
            return v;
        }
        return config.ExitThreshold;
    }

    /// <summary>
    /// True when the bar timestamp falls within the configured settlement-skip window
    /// after an OKX funding settlement boundary (00:00 / 08:00 / 16:00 UTC). Used to
    /// gate entries (NOT exits) — see FundingAlphaConfig.SkipSettlementBars rationale.
    /// </summary>
    public static bool IsSettlementBar(DateTime barTimestampUtc, FundingAlphaConfig config)
    {
        if (!config.SkipSettlementBars) return false;
        if (config.SettlementSkipMinutes <= 0) return false;
        // Settlement at hours 0, 8, 16 UTC. Skip when bar timestamp is within
        // SettlementSkipMinutes after any settlement hour.
        var ts = barTimestampUtc.ToUniversalTime();
        if (ts.Hour % 8 != 0) return false;
        return ts.Minute < config.SettlementSkipMinutes;
    }

    private static double ComputeSharpe(IReadOnlyList<FundingAlphaTrade> trades, decimal startingCapital)
    {
        if (trades.Count < 2 || startingCapital <= 0m) return 0.0;
        var returns = trades.Select(t => (double)(t.NetPnl / startingCapital)).ToArray();
        double mean = returns.Average();
        double variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Length - 1);
        double std = Math.Sqrt(variance);
        if (std < 1e-12) return 0.0;
        // 2026-04-30 audit fix: was hardcoded √1095 (assumes 3 funding cycles/day × 365).
        // Real entry-trigger rate is far lower (50-150 trades/year on funding alpha when
        // threshold ≥1bp), so the constant inflated reported Sharpe by ~3-5×. Use the
        // ACTUAL trade frequency observed in the sample. Fall back to √1095 only when
        // the trade timestamps span < 1 day (insufficient horizon to estimate rate).
        var firstEntry = trades.Min(t => t.EntryTimeUtc);
        var lastExit = trades.Max(t => t.ExitTimeUtc);
        var spanDays = (lastExit - firstEntry).TotalDays;
        var tradesPerYear = spanDays >= 1.0
            ? trades.Count / (spanDays / 365.0)
            : 1095.0;
        return (mean / std) * Math.Sqrt(tradesPerYear);
    }

    private sealed record OpenPosition(
        TradeSide Side,
        int EntryBar,
        DateTime EntryTimeUtc,
        decimal EntryPrice,
        decimal Qty,
        decimal FundingAtEntry);
}

public sealed record FundingAlphaConfig(
    decimal EntryThreshold = 0.0003m,          // 3 bp / 8h → ~1% annualized, real signal
    decimal ExitThreshold = 0.00005m,          // 0.5 bp / 8h → near zero
    decimal TakeProfitPct = 0.02m,             // 2% price move in our favor
    decimal StopLossPct = 0.015m,              // 1.5% adverse
    int MaxHoldingBars = 16,                   // 16h = 2 funding cycles
    decimal BaseSizeFraction = 0.05m,          // 5% equity base
    decimal MaxSizeFraction = 0.15m,           // 15% cap
    double StrengthScale = 1.0,                // 1:1 extra size per threshold of excess
    decimal MaxStrengthMult = 3.0m,            // cap conviction scaling
    decimal RoundTripCostRate = 0.0008m,       // 8 bps round-trip (OKX taker×2 + slippage)
    // --- Leverage / safety (2026-04-24) ---
    // Notional multiplier. 1.0 = unleveraged. Crypto perp liquidation typically at maint
    // margin ~0.5% so theoretical max ~200x — we HARD-CAP at 15x for safety. Apply at
    // position level; combined with MaxSizeFraction enforces a gross-exposure cap.
    // Formula: notional = equity × sizeFraction × leverage. Drawdown also scales linearly.
    decimal Leverage = 1.0m,
    decimal MaxLeverage = 15.0m,                // hard safety cap — never exceeded
    // Drawdown-based auto-throttle: when realized DD ≥ this fraction of MaxDrawdownBudget,
    // leverage is halved for subsequent entries until DD recovers. 0 disables throttle.
    decimal DrawdownThrottleFraction = 0.60m,
    decimal MaxDrawdownBudget = 0.05m,          // 5% portfolio DD budget

    // --- Funding flip catalyst overlay (2026-04-27 wiring sprint W1) ---
    // When true, AFTER the standard threshold check, also fire entries triggered by a
    // recent funding-rate sign flip (FundingFlipDetector). The flip is an event-driven
    // catalyst orthogonal to the threshold rule: a regime crossing zero indicates the
    // crowded side has just been flushed and price tends to mean-revert.
    bool UseFlipCatalyst = false,
    // Minimum |rate before flip| in bps to qualify (filters zero-noise flips).
    // Tightened 2026-04-27 from 0.5 → 3.0 after ablation showed 0.5 firing on noise
    // around zero in the current calm regime, dropping LTC Sharpe 2.29→0.47.
    decimal FlipMinMagnitudeBps = 3.0m,
    // Maximum age of flip event (bars on the candles array) to still count.
    // Tightened 2026-04-27 from 4 → 1 — only fire on the immediate flip bar, not stale
    // ones the threshold rule had multiple chances to act on.
    int FlipMaxAgeBars = 1,
    // Position size on flip-only signals as a fraction of BaseSizeFraction.
    // Smaller than threshold-based entries since this is event-driven, lower conviction.
    decimal FlipSizeMultiplier = 0.5m,

    // --- Per-symbol threshold override (2026-04-30 audit) ---
    // The default EntryThreshold (1bp) was calibrated against LTC's funding distribution.
    // In current calm-funding regimes, smaller-cap symbols (DOGE seen with 0 trades over
    // 6 days at 1bp) need a tighter threshold to fire. Without per-symbol override,
    // operators must lower the global threshold for everyone — re-introducing noise
    // entries on the larger symbols. This dictionary lets each symbol opt into a
    // tighter (or looser) threshold; missing keys fall back to EntryThreshold.
    // Format: "DOGEUSDT:0.00005,LTCUSDT:0.00010". Multiplier-based (e.g. 0.5x for half)
    // would risk silent magnitude drift across config edits — explicit absolute values
    // are auditable. Future Tier 1: regime-adaptive auto-scaling replaces this.
    System.Collections.Generic.Dictionary<string, decimal>? PerSymbolEntryThreshold = null,
    System.Collections.Generic.Dictionary<string, decimal>? PerSymbolExitThreshold = null,

    // --- Funding settlement bar mask (2026-04-30 audit) ---
    // OKX perpetual funding settles at fixed UTC times: 00:00, 08:00, 16:00. The bar
    // containing each settlement boundary tends to (a) carry a wider spread because
    // makers temporarily pull quotes around the funding payment, (b) show artificially
    // low volume in the first few minutes post-settle, and (c) have a discontinuous
    // funding rate (a fresh cycle just started). Backtests using the mark price treat
    // these bars as normal — live execution gets the real spread + worse fill.
    //
    // When true (default), entries are SKIPPED on bars whose timestamp falls in the
    // first SettlementSkipMinutes minutes after a settlement boundary. Exits are NOT
    // skipped — closing fast on a stop is more important than fee optimization.
    bool SkipSettlementBars = true,
    int SettlementSkipMinutes = 30);

public sealed record FundingAlphaTrade(
    TradeSide Side,
    DateTime EntryTimeUtc,
    DateTime ExitTimeUtc,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Qty,
    decimal NetPnl,
    decimal GrossPnl,
    decimal Costs,
    decimal FundingAtEntry,
    decimal FundingAtExit,
    string ExitReason);

public sealed record FundingAlphaResult(
    string Symbol,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    double WinRate,
    decimal TotalPnl,
    decimal GrossProfit,
    decimal GrossLoss,
    decimal ProfitFactor,
    double SharpeRatio,
    decimal MaxDrawdownPct,
    decimal NetPnl,
    decimal StartingCapital,
    decimal FinalEquity,
    int FundingObservations,
    int EntrySignals,
    double AvgFunding,
    double MaxAbsFunding,
    IReadOnlyList<FundingAlphaTrade> Trades,
    FundingLiveSignal LiveSignal)
{
    public static FundingAlphaResult Empty(string symbol, string reason) =>
        new(symbol, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            Array.Empty<FundingAlphaTrade>(), FundingLiveSignal.None);
}

public enum FundingLiveSignalKind
{
    None,
    HoldOpen,
    WouldOpenNew,
    WouldClose
}

public sealed record FundingLiveSignal(
    FundingLiveSignalKind Kind,
    TradeSide Side,
    decimal Qty,
    decimal ReferencePrice,
    decimal CurrentFunding,
    string Reasoning)
{
    public static FundingLiveSignal None => new(FundingLiveSignalKind.None, TradeSide.Buy, 0m, 0m, 0m, "no signal");
}
