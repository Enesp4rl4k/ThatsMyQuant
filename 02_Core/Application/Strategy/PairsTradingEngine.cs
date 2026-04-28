using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using MediatR;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Statistical-arbitrage pairs trading engine built around Engle-Granger cointegration
/// with full professional-grade diagnostics: Hurst-filtered pair admission, rolling
/// z-score (regime-adaptive), cost-aware entry gate, quality-weighted confidence,
/// OU-calibrated half-life sizing, and leg-coordinated execution.
///
/// Life-cycle:
///   1. Tick stream updates prices for SymbolA (Y) and SymbolB (X).
///   2. Every <see cref="PairsTradingOptions.RebalanceInterval"/> ticks, run
///      EG cointegration + Hurst + correlation. Admit or reject the pair.
///   3. When admitted, compute rolling spread z-score each tick.
///   4. Entry: |z| ≥ EntryZScoreThreshold AND expected edge ≥ cost multiple AND
///      half-life in range AND hedge ratio sane.
///   5. Exit: mean reversion, stop-loss, max-hold (half-life × multiplier), or
///      cointegration break.
/// </summary>
public class PairsTradingEngine : INotificationHandler<Signal>, IPairDiagnosticsProvider
{
    private readonly ILogger<PairsTradingEngine> _logger;
    private readonly IMediator _mediator;
    private readonly PairsTradingOptions _options;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    // Serializes tick-stream mutations. MediatR can dispatch Signal notifications for
    // symbol A and symbol B on different threads; without this gate _latestPriceA/B,
    // rolling windows, z-score state, and _position would race under live load.
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private KalmanHedgeRatioFilter? _kalman;

    private readonly string _symbolA;
    private readonly string _symbolB;

    // Rolling price window for periodic cointegration tests.
    private readonly decimal[] _priceWindowY;
    private readonly decimal[] _priceWindowX;
    private int _historyIndex = 0;
    private int _historyCount = 0;
    private readonly int _windowSize;

    // Rolling spread window for live z-score (independent of the coint-test window).
    private readonly decimal[] _spreadWindow;
    private int _spreadIndex = 0;
    private int _spreadCount = 0;
    private readonly int _zScoreWindow;

    private decimal _latestPriceA;
    private decimal _latestPriceB;

    private bool _isCointegrated;
    private decimal _hedgeRatio = 1m;
    private decimal _spreadMean;        // From cointegration test (fallback).
    private decimal _spreadStdDev;
    private double _halfLife = double.PositiveInfinity;
    private double _hurstExponent = 0.5;
    private double _correlation = 0.0;
    private double _adfTStat = 0.0;
    private int _tickCounter = 0;
    private int _lastSignalTick = int.MinValue / 2;

    private PairPositionState _position = PairPositionState.Flat;
    private int _entryTick = -1;
    private double _entryZScore = 0.0;
    private int _entryConfidence = 0;

    // Latest tick-level diagnostics exposed via snapshot.
    private decimal _currentSpread;
    private double _currentZScore;

    [ActivatorUtilitiesConstructor]
    public PairsTradingEngine(
        ILogger<PairsTradingEngine> logger,
        IMediator mediator,
        IOptions<PairsTradingOptions>? options = null)
    {
        _options = options?.Value ?? new PairsTradingOptions();
        _symbolA = _options.SymbolA;
        _symbolB = _options.SymbolB;
        _windowSize = Math.Max(30, _options.WindowSize);
        _priceWindowY = new decimal[_windowSize];
        _priceWindowX = new decimal[_windowSize];
        _zScoreWindow = Math.Max(20, _options.ZScoreWindow);
        _spreadWindow = new decimal[_zScoreWindow];
        _logger = logger;
        _mediator = mediator;
    }

    internal PairsTradingEngine(string symbolA, string symbolB, int windowSize, ILogger<PairsTradingEngine> logger, IMediator mediator)
        : this(
            logger,
            mediator,
            Options.Create(new PairsTradingOptions
            {
                SymbolA = symbolA,
                SymbolB = symbolB,
                WindowSize = windowSize
            }))
    {
    }

    /// <summary>Current pair diagnostics. Safe to read from any thread — snapshot is a
    /// value-copy at call time.</summary>
    public PairDiagnosticSnapshot GetDiagnostics() => new(
        SymbolA: _symbolA,
        SymbolB: _symbolB,
        IsCointegrated: _isCointegrated,
        HedgeRatio: _hedgeRatio,
        HalfLife: _halfLife,
        HurstExponent: _hurstExponent,
        Correlation: _correlation,
        AdfTStatistic: _adfTStat,
        CurrentSpread: _currentSpread,
        CurrentZScore: _currentZScore,
        Position: _position.ToString(),
        TickCount: _tickCounter);

    public async Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        await ProcessTickAsync(notification.Symbol, notification.Price, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessTickAsync(string symbol, decimal price, CancellationToken ct)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool updated = false;

            if (symbol == _symbolA)
            {
                _latestPriceA = price;
                updated = true;
            }
            else if (symbol == _symbolB)
            {
                _latestPriceB = price;
                updated = true;
            }

            if (!updated || _latestPriceA <= 0 || _latestPriceB <= 0) return;

            _tickCounter++;

            _priceWindowY[_historyIndex] = _latestPriceA;
            _priceWindowX[_historyIndex] = _latestPriceB;
            _historyIndex = (_historyIndex + 1) % _windowSize;
            if (_historyCount < _windowSize) _historyCount++;

            if (_historyCount == _windowSize && _tickCounter % Math.Max(1, _options.RebalanceInterval) == 0)
                RecomputeCointegration();

            // NB: cointegration may have just flipped false (via RecomputeCointegration).
            // If we hold an open position we still want EvaluateExit to run so it can
            // fire the CointegrationBreak exit — do NOT early-return on the flat path.
            if (!_isCointegrated && _position == PairPositionState.Flat) return;

            // Kalman β update — one multiply/add per tick, keeps β tracking regime drift
            // between the (expensive) periodic OLS rebalances. Only update when admitted.
            if (_isCointegrated && _options.UseKalmanHedgeRatio && _kalman is not null)
            {
                _kalman.Update((double)_latestPriceA, (double)_latestPriceB);
                _hedgeRatio = (decimal)_kalman.Beta;
            }

            // Spread update every tick (regardless of rebalance cadence).
            _currentSpread = _latestPriceA - (_hedgeRatio * _latestPriceB);
            PushSpread(_currentSpread);
            _currentZScore = ComputeZScore();

            await EvaluateSpreadAsync(_currentZScore, ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private void PushSpread(decimal spread)
    {
        _spreadWindow[_spreadIndex] = spread;
        _spreadIndex = (_spreadIndex + 1) % _zScoreWindow;
        if (_spreadCount < _zScoreWindow) _spreadCount++;
    }

    /// <summary>
    /// Rolling z-score over the most recent <see cref="_zScoreWindow"/> spread samples.
    /// Falls back to cointegration-captured mean/std when the rolling buffer is cold or
    /// when <see cref="PairsTradingOptions.UseRollingZScore"/> is false.
    /// </summary>
    private double ComputeZScore()
    {
        if (!_options.UseRollingZScore || _spreadCount < Math.Min(_zScoreWindow, 20))
        {
            if (_spreadStdDev == 0m) return 0.0;
            return (double)((_currentSpread - _spreadMean) / _spreadStdDev);
        }

        decimal sum = 0m;
        for (int i = 0; i < _spreadCount; i++) sum += _spreadWindow[i];
        decimal mean = sum / _spreadCount;

        decimal sumSq = 0m;
        for (int i = 0; i < _spreadCount; i++)
        {
            decimal d = _spreadWindow[i] - mean;
            sumSq += d * d;
        }
        decimal std = (decimal)Math.Sqrt((double)(sumSq / Math.Max(1, _spreadCount - 1)));
        if (std == 0m) return 0.0;

        return (double)((_currentSpread - mean) / std);
    }

    private void RecomputeCointegration()
    {
        var seriesY = new decimal[_windowSize];
        var seriesX = new decimal[_windowSize];
        for (int i = 0; i < _windowSize; i++)
        {
            int idx = (_historyIndex + i) % _windowSize;
            seriesY[i] = _priceWindowY[idx];
            seriesX[i] = _priceWindowX[idx];
        }

        var result = EngleGrangerDetector.TestCointegration(seriesY, seriesX);

        // Store raw diagnostics even when not admitted — useful for telemetry.
        _hedgeRatio = result.HedgeRatio;
        _spreadMean = result.SpreadAverage;
        _spreadStdDev = Math.Max(0.0001m, result.SpreadStdDev);
        _halfLife = result.HalfLife;
        _hurstExponent = result.HurstExponent;
        _correlation = result.Correlation;
        _adfTStat = result.AdfTStatistic;

        // Admission: EngleGrangerDetector already ANDs ADF + (Hurst if reliable) + |corr|.
        // Apply user-tunable tightening from options on top of the built-in gate.
        // Hurst is only reliable at window ≥ 200 due to R/S small-sample bias.
        bool hurstReliable = _windowSize >= 200;
        bool optionsGatePassed =
            Math.Abs(_correlation) >= _options.MinAbsCorrelation &&
            (!hurstReliable || _hurstExponent <= _options.MaxHurstExponent);

        _isCointegrated = result.IsCointegrated && optionsGatePassed;

        // Seed / re-seed Kalman filter from the fresh OLS estimate. Process noise Q
        // and observation noise R (≈ residual variance) come from options/result.
        if (_options.UseKalmanHedgeRatio && _isCointegrated)
        {
            double obsVar = Math.Max(1e-9, (double)(_spreadStdDev * _spreadStdDev));
            if (_kalman is null)
            {
                _kalman = new KalmanHedgeRatioFilter(
                    initialBeta: (double)_hedgeRatio,
                    initialVariance: _options.KalmanInitialVariance,
                    processVariance: _options.KalmanProcessVariance,
                    observationVariance: obsVar);
            }
            else
            {
                _kalman.Reseed((double)_hedgeRatio, _options.KalmanInitialVariance * 0.25);
                _kalman.SetObservationVariance(obsVar);
            }
        }

        if (_isCointegrated)
        {
            _logger.LogInformation(
                "Pair {A}/{B} ADMITTED | β={Beta:F3} HL={HL:F1} H={H:F2} ρ={Rho:F2} tADF={T:F2}",
                _symbolA, _symbolB, _hedgeRatio, _halfLife, _hurstExponent, _correlation, _adfTStat);
        }
        else if (_position != PairPositionState.Flat)
        {
            // Open position and pair just failed admission — flag for exit via EvaluateExit.
            _logger.LogWarning(
                "Pair {A}/{B} REJECTED but position open | H={H:F2} ρ={Rho:F2} — forcing exit next tick",
                _symbolA, _symbolB, _hurstExponent, _correlation);
        }
    }

    /// <summary>
    /// Quality-weighted pair signal confidence. Replaces the old hardcoded 95 with a
    /// number grounded in the pair's measured statistical properties.
    ///
    ///   Base 50 + up to 50 from:
    ///     • Hurst quality (lower = better mean reversion)           — 15 pts
    ///     • |Correlation| (higher = cleaner relationship)           — 10 pts
    ///     • Half-life sweet spot (target 5-30 bars)                 — 10 pts
    ///     • |ADF t-stat| strength (more negative = stronger signal) — 10 pts
    ///     • |Z-score| magnitude (deeper = stronger mean-revert edge) —  5 pts
    /// </summary>
    private int ComputePairConfidence(double zScore)
    {
        int score = 50;

        // Hurst: 0.30 → 15 pts, 0.45 → 8 pts, 0.55 → 0 pts. Below 0.30 capped.
        double hurstScore = _hurstExponent <= 0.30 ? 15
            : _hurstExponent <= 0.40 ? 12
            : _hurstExponent <= 0.45 ? 8
            : _hurstExponent <= 0.50 ? 4
            : 0;
        score += (int)hurstScore;

        // |Correlation|: 0.80+ → 10, 0.60+ → 7, 0.40+ → 4, else 0.
        double absCorr = Math.Abs(_correlation);
        int corrScore = absCorr >= 0.80 ? 10 : absCorr >= 0.60 ? 7 : absCorr >= 0.40 ? 4 : 0;
        score += corrScore;

        // Half-life sweet spot (5-30 bars). Outside penalized but not zeroed.
        int hlScore = _halfLife switch
        {
            >= 5.0 and <= 30.0 => 10,
            >= 3.0 and <= 50.0 => 6,
            >= 2.0 and <= 80.0 => 3,
            _ => 0
        };
        score += hlScore;

        // ADF t-stat: more negative = stronger stationarity evidence.
        int adfScore = _adfTStat <= -3.5 ? 10
            : _adfTStat <= -2.5 ? 7
            : _adfTStat <= -1.5 ? 4
            : _adfTStat <= -0.5 ? 1
            : 0;
        score += adfScore;

        // |Z-score| magnitude — stronger signal when spread is deeper.
        double absZ = Math.Abs(zScore);
        int zScoreBonus = absZ >= 3.0 ? 5 : absZ >= 2.5 ? 3 : absZ >= 2.0 ? 2 : 0;
        score += zScoreBonus;

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// Cost-aware entry gate. Expected reversion profit ≈ |z| × spread_std × exposure.
    /// Round-trip cost ≈ cost_rate × gross_notional. We require the expected profit
    /// to exceed MinEdgeToCostRatio × cost_of_trade — protects from tight-spread
    /// negative-expectancy setups.
    /// </summary>
    private bool PassesCostAwareEntry(double zScore)
    {
        double absZ = Math.Abs(zScore);
        if (absZ < _options.EntryZScoreThreshold) return false;

        // Expected edge = how far we expect the spread to move to the mean, scaled by
        // the ratio of standard deviations to price (so it's comparable to cost%).
        double refPrice = Math.Max(1e-9, (double)_latestPriceA);
        double edgePct = absZ * (double)_spreadStdDev / refPrice;  // fraction
        double costPct = (double)_options.EstimatedRoundTripCostRate;
        double required = costPct * _options.MinEdgeToCostRatio;

        if (edgePct < required)
        {
            _logger.LogDebug(
                "Pair {A}/{B} cost-aware reject: edge {Edge:P4} < required {Req:P4} (|z|={Z:F2})",
                _symbolA, _symbolB, edgePct, required, absZ);
            return false;
        }
        return true;
    }

    private async Task EvaluateSpreadAsync(double zScore, CancellationToken ct)
    {
        // Exit logic first — open positions take priority over new entries.
        if (_position != PairPositionState.Flat)
        {
            await EvaluateExitAsync(zScore, ct).ConfigureAwait(false);
            return;
        }

        if (_tickCounter - _lastSignalTick < Math.Max(1, _options.SignalCooldownTicks)) return;

        // Half-life band check: too fast or too slow = no edge.
        if (_halfLife > _options.MaxHalfLifeBars ||
            _halfLife < _options.MinHalfLifeBars ||
            double.IsInfinity(_halfLife) || double.IsNaN(_halfLife))
            return;

        if (!PassesCostAwareEntry(zScore)) return;

        int confidence = ComputePairConfidence(zScore);
        if (confidence < _options.MinSignalConfidence)
        {
            _logger.LogDebug("Pair {A}/{B} signal suppressed: confidence {C} < floor {F}",
                _symbolA, _symbolB, confidence, _options.MinSignalConfidence);
            return;
        }

        if (zScore > ZScoreThresholdEntry)
        {
            _logger.LogInformation(
                "PAIRS ENTRY (Short-Spread): Short {A} / Long {B} | z={Z:F2} HL={HL:F1} conf={C}",
                _symbolA, _symbolB, zScore, _halfLife, confidence);
            await ExecuteLegsAsync(TradeSide.Sell, TradeSide.Buy, zScore, confidence, isEntry: true, ct).ConfigureAwait(false);
            _position = PairPositionState.ShortSpread;
            _entryTick = _tickCounter;
            _entryZScore = zScore;
            _entryConfidence = confidence;
        }
        else if (zScore < -ZScoreThresholdEntry)
        {
            _logger.LogInformation(
                "PAIRS ENTRY (Long-Spread): Long {A} / Short {B} | z={Z:F2} HL={HL:F1} conf={C}",
                _symbolA, _symbolB, zScore, _halfLife, confidence);
            await ExecuteLegsAsync(TradeSide.Buy, TradeSide.Sell, zScore, confidence, isEntry: true, ct).ConfigureAwait(false);
            _position = PairPositionState.LongSpread;
            _entryTick = _tickCounter;
            _entryZScore = zScore;
            _entryConfidence = confidence;
        }
    }

    private async Task EvaluateExitAsync(double zScore, CancellationToken ct)
    {
        bool shouldExit = false;
        string exitReason = string.Empty;

        double exitThreshold = _options.ExitZScoreThreshold > 0 ? _options.ExitZScoreThreshold : 0.5;
        double stopLossThreshold = _options.StopLossZScoreThreshold > 0 ? _options.StopLossZScoreThreshold : 3.5;

        // 1. Mean reversion — profit exit.
        if (_position == PairPositionState.ShortSpread && zScore <= exitThreshold)
        { shouldExit = true; exitReason = "MeanReversion"; }
        else if (_position == PairPositionState.LongSpread && zScore >= -exitThreshold)
        { shouldExit = true; exitReason = "MeanReversion"; }

        // 2. Stop-loss — cointegration breaking down while we're in.
        if (!shouldExit)
        {
            if (_position == PairPositionState.ShortSpread && zScore > stopLossThreshold)
            { shouldExit = true; exitReason = "StopLoss"; }
            else if (_position == PairPositionState.LongSpread && zScore < -stopLossThreshold)
            { shouldExit = true; exitReason = "StopLoss"; }
        }

        // 3. Max holding — half-life × multiplier.
        if (!shouldExit && _halfLife > 0 && !double.IsInfinity(_halfLife) && !double.IsNaN(_halfLife))
        {
            double maxHold = Math.Min(
                _halfLife * _options.MaxHoldingHalfLifeMultiplier,
                _options.MaxHoldingBars > 0 ? _options.MaxHoldingBars : double.MaxValue);
            if (_tickCounter - _entryTick > maxHold)
            { shouldExit = true; exitReason = "MaxHoldTime"; }
        }

        // 4. Hard exit — admission lost (pair no longer cointegrates).
        if (!shouldExit && !_isCointegrated)
        { shouldExit = true; exitReason = "CointegrationBreak"; }

        // 5. Hurst drift — spread became trending while we were in.
        if (!shouldExit && _hurstExponent > _options.MaxHurstExponent + 0.05)
        { shouldExit = true; exitReason = "HurstDrift"; }

        if (!shouldExit) return;

        _logger.LogInformation(
            "PAIRS EXIT ({Reason}): Close {A}/{B} | entryZ={EZ:F2} exitZ={XZ:F2} bars={B} entryConf={C}",
            exitReason, _symbolA, _symbolB, _entryZScore, zScore, _tickCounter - _entryTick, _entryConfidence);

        var sideA = _position == PairPositionState.ShortSpread ? TradeSide.Buy : TradeSide.Sell;
        var sideB = _position == PairPositionState.ShortSpread ? TradeSide.Sell : TradeSide.Buy;

        await ExecuteLegsAsync(sideA, sideB, zScore, _entryConfidence, isEntry: false, ct).ConfigureAwait(false);

        _position = PairPositionState.Flat;
        _entryTick = -1;
        _entryZScore = 0.0;
        _entryConfidence = 0;
    }

    /// <summary>
    /// Dual-leg execution with post-hoc coordination: if one leg fails while the other
    /// fills, fire a compensating order on the successful leg to avoid a naked position.
    /// In paper/simulation modes ExecuteTradeCommand returns synchronously, so this
    /// is essentially zero-cost on happy paths.
    /// </summary>
    private async Task ExecuteLegsAsync(
        TradeSide sideA,
        TradeSide sideB,
        double zScore,
        int confidence,
        bool isEntry,
        CancellationToken ct)
    {
        await _executionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tickCounter - _lastSignalTick < Math.Max(1, _options.SignalCooldownTicks)) return;
            _lastSignalTick = _tickCounter;

            // Half-life-aware sizing: shorter HL (faster reversion) deserves more size.
            var halfLifeMultiplier = _halfLife <= 10
                ? 1.0
                : Math.Max(0.3, 1.0 - (_halfLife - 10.0) / 150.0);
            var coordinatorScale = Math.Max(0m, _options.CoordinatorAllocationScale);
            var baseAllocation = Math.Max(100m, _options.BaseAllocationUsd * (decimal)halfLifeMultiplier * coordinatorScale);

            var absBeta = Math.Abs(_hedgeRatio);
            if (absBeta < _options.MinHedgeRatio || absBeta > _options.MaxHedgeRatio)
            {
                _logger.LogDebug("Pairs execution skipped: |β|={Beta:F3} out of [{Lo},{Hi}]",
                    absBeta, _options.MinHedgeRatio, _options.MaxHedgeRatio);
                return;
            }

            string tag = isEntry ? "PairsEntry" : "PairsExit";
            string reasoning = $"{tag} β={_hedgeRatio:F3} z={zScore:F2} HL={_halfLife:F1} " +
                               $"H={_hurstExponent:F2} ρ={_correlation:F2} conf={confidence}";

            var sigA = new Signal
            {
                Symbol = _symbolA,
                Price = _latestPriceA,
                Side = sideA,
                StrategyTag = "PairsTrading",
                Confidence = confidence,
                Reasoning = reasoning,
                Quantity = baseAllocation / Math.Max(0.0000001m, _latestPriceA)
            };

            var sigB = new Signal
            {
                Symbol = _symbolB,
                Price = _latestPriceB,
                Side = sideB,
                StrategyTag = "PairsTrading",
                Confidence = confidence,
                Reasoning = reasoning,
                Quantity = (baseAllocation * absBeta) / Math.Max(0.0000001m, _latestPriceB)
            };

            // Coordinated execution: wait for both, capture individual exceptions so we
            // can compensate on partial fill.
            var legATask = _mediator.Send(new ExecuteTradeCommand(sigA), ct);
            var legBTask = _mediator.Send(new ExecuteTradeCommand(sigB), ct);
            bool aOk = false, bOk = false;
            try { await legATask.ConfigureAwait(false); aOk = true; }
            catch (Exception ex) { _logger.LogError(ex, "Pair leg A failed: {Sym}", _symbolA); }
            try { await legBTask.ConfigureAwait(false); bOk = true; }
            catch (Exception ex) { _logger.LogError(ex, "Pair leg B failed: {Sym}", _symbolB); }

            if (aOk ^ bOk)
            {
                // Partial fill — compensate the surviving leg to stay flat.
                _logger.LogError(
                    "PAIR LEG MISMATCH ({Phase}): {A}={Aok} {B}={Bok} — emitting compensating order on survivor",
                    tag, _symbolA, aOk, _symbolB, bOk);
                var survivorSig = aOk
                    ? new Signal
                        {
                            Symbol = sigA.Symbol,
                            Price = sigA.Price,
                            Side = Flip(sideA),
                            StrategyTag = sigA.StrategyTag,
                            Confidence = sigA.Confidence,
                            Reasoning = reasoning + " | LegCompensation",
                            Quantity = sigA.Quantity
                        }
                    : new Signal
                        {
                            Symbol = sigB.Symbol,
                            Price = sigB.Price,
                            Side = Flip(sideB),
                            StrategyTag = sigB.StrategyTag,
                            Confidence = sigB.Confidence,
                            Reasoning = reasoning + " | LegCompensation",
                            Quantity = sigB.Quantity
                        };
                try { await _mediator.Send(new ExecuteTradeCommand(survivorSig), ct).ConfigureAwait(false); }
                catch (Exception ex2) { _logger.LogCritical(ex2, "COMPENSATION FAILED — manual intervention required for {Sym}", survivorSig.Symbol); }
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static TradeSide Flip(TradeSide s) => s == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;

    private double ZScoreThresholdEntry => _options.EntryZScoreThreshold > 0 ? _options.EntryZScoreThreshold : 2.0;
}

internal enum PairPositionState
{
    Flat = 0,
    LongSpread = 1,
    ShortSpread = 2,
}

/// <summary>Point-in-time snapshot of a pair engine's statistical state and open position.
/// Exposed for dashboards, telemetry, and portfolio-level coordination.</summary>
public sealed record PairDiagnosticSnapshot(
    string SymbolA,
    string SymbolB,
    bool IsCointegrated,
    decimal HedgeRatio,
    double HalfLife,
    double HurstExponent,
    double Correlation,
    double AdfTStatistic,
    decimal CurrentSpread,
    double CurrentZScore,
    string Position,
    int TickCount);
