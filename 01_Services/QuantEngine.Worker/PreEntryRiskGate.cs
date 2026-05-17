using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Bot;

/// <summary>
/// [2026-05-14 unattended hardening] Singleton pre-entry safety net consulted by every
/// executor BEFORE submitting a new position to the venue. Three independent checks:
///
///   1. <b>Fat-finger</b>: single position notional must not exceed <c>MaxPositionPctOfBasis</c>
///      (default 15%) of the basis. Catches sizing bugs that would inadvertently
///      open one giant position vs the intended small slice.
///
///   2. <b>Position-count ceiling</b>: total open positions must not exceed
///      <c>MaxOpenPositions</c> (default 10). Catches runaway entry loops where a
///      strategy fires repeatedly without seeing the existing positions.
///
///   3. <b>Funding rate sanity</b>: current observed funding must be within
///      <c>MaxFundingDriftBps</c> (default 100bp = 1%) of the signal's funding-at-decision
///      time. Catches stale signals that would enter at a funding regime far from
///      the one the strategy was reasoning about.
///
/// All checks return a <see cref="GateDecision"/>; the executor decides whether to
/// proceed. Logged on rejection at WARNING level for unattended visibility.
///
/// Independent of <see cref="KillSwitchHostedService"/> (which is post-event flatten)
/// and <see cref="DrawdownLadderGate"/> (which is graduated entry freeze). This is a
/// per-entry guard that catches single-trade catastrophes before they happen.
/// </summary>
public sealed class PreEntryRiskGate
{
    private readonly ILogger<PreEntryRiskGate> _logger;
    private readonly PaperPortfolio _portfolio;

    private readonly decimal _maxPositionPctOfBasis;
    private readonly int _maxOpenPositions;
    private readonly decimal _maxFundingDriftBps;
    private readonly bool _enabled;

    public PreEntryRiskGate(
        ILogger<PreEntryRiskGate> logger,
        IConfiguration cfg,
        PaperPortfolio portfolio)
    {
        _logger = logger;
        _portfolio = portfolio;

        _enabled = !bool.TryParse(cfg["Risk:PreEntry:Enabled"], out var en) || en;
        _maxPositionPctOfBasis = decimal.TryParse(
            cfg["Risk:PreEntry:MaxPositionPctOfBasis"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var mp) && mp > 0 ? mp : 0.15m;
        _maxOpenPositions = int.TryParse(cfg["Risk:PreEntry:MaxOpenPositions"], out var mo) && mo > 0 ? mo : 10;
        _maxFundingDriftBps = decimal.TryParse(
            cfg["Risk:PreEntry:MaxFundingDriftBps"],
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var fd) && fd > 0 ? fd : 100m;

        _logger.LogInformation(
            "PreEntryRiskGate initialised. Enabled={En} MaxPositionPct={MP:P1} MaxOpen={MO} MaxFundingDrift={FD:F0}bp",
            _enabled, _maxPositionPctOfBasis, _maxOpenPositions, _maxFundingDriftBps);
    }

    public readonly record struct GateDecision(bool Allowed, string? RejectReason)
    {
        public static GateDecision Allow() => new(true, null);
        public static GateDecision Reject(string reason) => new(false, reason);
    }

    /// <summary>
    /// Check fat-finger, position-count, and macro-event blackout guards. Funding-rate
    /// sanity is a separate overload because it requires signal-time funding data;
    /// not all callers have it.
    /// </summary>
    public GateDecision CheckEntry(string symbol, decimal positionNotional)
    {
        if (!_enabled) return GateDecision.Allow();

        decimal basis;
        int openCount;
        lock (_portfolio)
        {
            basis = _portfolio.InitialBalance;
            openCount = _portfolio.OpenPositions.Count;
        }

        if (basis <= 0m)
        {
            // Basis not yet set — refuse to evaluate (caller should also refuse to size).
            return GateDecision.Reject($"PreEntry: basis not initialised yet (basis=0)");
        }

        // 1) Fat-finger
        var positionPct = positionNotional / basis;
        if (positionPct > _maxPositionPctOfBasis)
        {
            var reason = $"PreEntry FAT-FINGER: {symbol} notional ${positionNotional:N2} = {positionPct:P1} of basis ${basis:N2} > limit {_maxPositionPctOfBasis:P0}";
            _logger.LogWarning(reason);
            return GateDecision.Reject(reason);
        }

        // 2) Position-count ceiling
        if (openCount >= _maxOpenPositions)
        {
            var reason = $"PreEntry POSITION-COUNT: {openCount} open >= limit {_maxOpenPositions} (refused new entry on {symbol})";
            _logger.LogWarning(reason);
            return GateDecision.Reject(reason);
        }

        // 3) Macro event blackout (B4 from optimization plan)
        // Read MacroEventCalendarService's static Current — no DI dependency, no race
        // (volatile field). Pause state is updated every refresh tick (~30min) and on
        // every PeriodicTimer tick. Pre/PostEvent windows are configured in News:Enabled section.
        var macroPause = MacroEventCalendarService.Current;
        if (macroPause.IsPaused)
        {
            var reason = $"PreEntry MACRO-BLACKOUT: {macroPause.EventName} {macroPause.Phase} (event @ {macroPause.EventAtUtc:HH:mm}Z, T-{macroPause.MinutesUntilEvent}m)";
            _logger.LogWarning(reason);
            return GateDecision.Reject(reason);
        }

        return GateDecision.Allow();
    }

    /// <summary>
    /// Funding-rate sanity overload. Caller passes the funding observed at signal-time
    /// vs the funding observed now (just before entry). If the absolute drift exceeds
    /// <see cref="_maxFundingDriftBps"/>, the signal is stale relative to its own thesis.
    /// </summary>
    public GateDecision CheckFundingDrift(string symbol, decimal signalTimeFundingBps, decimal currentFundingBps)
    {
        if (!_enabled) return GateDecision.Allow();
        var drift = Math.Abs(currentFundingBps - signalTimeFundingBps);
        if (drift > _maxFundingDriftBps)
        {
            var reason = $"PreEntry FUNDING-DRIFT: {symbol} signal funding {signalTimeFundingBps:F1}bp vs now {currentFundingBps:F1}bp drift {drift:F1}bp > limit {_maxFundingDriftBps:F0}bp";
            _logger.LogWarning(reason);
            return GateDecision.Reject(reason);
        }
        return GateDecision.Allow();
    }
}
