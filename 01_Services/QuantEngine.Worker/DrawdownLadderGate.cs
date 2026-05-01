using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Tiered drawdown kill-switch — operator-psychology guard from the 2026-05-01 risk
/// playbook. Existing GlobalCircuitBreaker handles a single per-day loss threshold;
/// this is the LADDER that scales the response based on drawdown depth, intentionally
/// pre-committed in code so a panicked operator cannot just "disable for one trade".
///
/// Tiers (defaults, configurable under Risk:DrawdownLadder):
///   Tier 1: drawdown ≥ -3% basis  → 24-hour entry freeze (existing positions managed normally)
///   Tier 2: drawdown ≥ -6% basis  → 72-hour entry freeze + critical alert
///   Tier 3: drawdown ≥ -10% basis → indefinite freeze; requires operator post-mortem
///                                    (write a markdown note + reset via /reset-drawdown command)
///
/// Drawdown is measured as `(equity_now - peak_equity) / basis`, peak_equity tracked
/// since process start (or persisted across restarts). The gate is a SINGLETON consulted
/// by every executor's HandleOpenAsync; existing positions continue to be managed (close
/// orders are NOT blocked even at Tier 3 — only new entries).
///
/// Reset semantics:
///   - Auto-reset when drawdown recovers ABOVE -2% (one tier below Tier 1) for ≥ 2 hours.
///   - Manual reset via TelegramCommandHostedService /reset-drawdown (requires explicit
///     operator action — this is the post-mortem checkpoint).
/// </summary>
public sealed class DrawdownLadderGate
{
    private readonly ILogger<DrawdownLadderGate> _logger;
    private readonly IRealtimeAlerter? _alerter;
    private readonly object _lock = new();

    private readonly decimal _tier1Pct;        // e.g. 0.03 = -3%
    private readonly decimal _tier2Pct;        // e.g. 0.06 = -6%
    private readonly decimal _tier3Pct;        // e.g. 0.10 = -10%
    private readonly TimeSpan _tier1FreezeDuration; // e.g. 24h
    private readonly TimeSpan _tier2FreezeDuration; // e.g. 72h
    private readonly decimal _autoResetRecoveryPct; // e.g. 0.02 = drawdown < -2%

    private decimal _peakEquity;
    private decimal _basis;
    private DrawdownTier _currentTier = DrawdownTier.None;
    private DateTime _tierEnteredUtc;
    private DateTime _autoResetEligibleUtc = DateTime.MaxValue;
    private string _lastTriggerNote = string.Empty;

    public DrawdownLadderGate(IConfiguration cfg, ILogger<DrawdownLadderGate> logger, IRealtimeAlerter? alerter = null)
    {
        _logger = logger;
        _alerter = alerter;

        _tier1Pct = ReadDecimal(cfg, "Risk:DrawdownLadder:Tier1Pct", 0.03m);
        _tier2Pct = ReadDecimal(cfg, "Risk:DrawdownLadder:Tier2Pct", 0.06m);
        _tier3Pct = ReadDecimal(cfg, "Risk:DrawdownLadder:Tier3Pct", 0.10m);
        _tier1FreezeDuration = TimeSpan.FromHours(ReadDouble(cfg, "Risk:DrawdownLadder:Tier1FreezeHours", 24.0));
        _tier2FreezeDuration = TimeSpan.FromHours(ReadDouble(cfg, "Risk:DrawdownLadder:Tier2FreezeHours", 72.0));
        _autoResetRecoveryPct = ReadDecimal(cfg, "Risk:DrawdownLadder:AutoResetRecoveryPct", 0.02m);

        _logger.LogInformation(
            "DrawdownLadderGate initialised. Tiers: -{T1:P1} ({F1:F0}h) / -{T2:P1} ({F2:F0}h) / -{T3:P1} (manual). Auto-reset above -{Reset:P1} for 2h.",
            _tier1Pct, _tier1FreezeDuration.TotalHours, _tier2Pct, _tier2FreezeDuration.TotalHours, _tier3Pct, _autoResetRecoveryPct);
    }

    /// <summary>
    /// Initialize with the trading basis (typically PaperPortfolio.InitialBalance). Should be
    /// called once at startup; subsequent calls are no-ops unless basis genuinely changes.
    /// </summary>
    public void Initialize(decimal basis, decimal currentEquity)
    {
        lock (_lock)
        {
            if (_basis > 0m && Math.Abs(basis - _basis) < 0.01m) return;
            _basis = basis;
            _peakEquity = Math.Max(currentEquity, basis);
            _logger.LogInformation("DrawdownLadderGate initialized: basis=${Basis:N2} peakEquity=${Peak:N2}", basis, _peakEquity);
        }
    }

    /// <summary>
    /// Called by every executor BEFORE opening a new position. Returns true when entry
    /// is permitted; false when frozen by the active tier. Existing positions / closes
    /// are NEVER blocked — flatten path stays open even at Tier 3.
    /// </summary>
    public bool CanOpenEntry(decimal currentEquity, out string blockReason)
    {
        lock (_lock)
        {
            UpdateTierState(currentEquity);
            blockReason = _currentTier switch
            {
                DrawdownTier.Tier1 => $"Drawdown -{_tier1Pct:P1} reached → Tier 1 entry freeze for {_tier1FreezeDuration.TotalHours:F0}h",
                DrawdownTier.Tier2 => $"Drawdown -{_tier2Pct:P1} reached → Tier 2 freeze for {_tier2FreezeDuration.TotalHours:F0}h",
                DrawdownTier.Tier3 => $"Drawdown -{_tier3Pct:P1} reached → indefinite freeze (manual reset required)",
                _ => string.Empty
            };
            return _currentTier == DrawdownTier.None;
        }
    }

    /// <summary>Diagnostic snapshot for dashboards / alerts.</summary>
    public DrawdownLadderStatus Snapshot()
    {
        lock (_lock)
        {
            var dd = _basis > 0m ? (_peakEquity - GetCurrentEquityFromCache()) / _basis : 0m;
            return new DrawdownLadderStatus(
                Basis: _basis,
                PeakEquity: _peakEquity,
                CurrentDrawdownPct: dd,
                ActiveTier: _currentTier,
                TierEnteredUtc: _tierEnteredUtc,
                AutoResetEligibleUtc: _autoResetEligibleUtc,
                LastTriggerNote: _lastTriggerNote);
        }
    }

    /// <summary>
    /// Manual operator reset — the Tier 3 escape hatch. Requires explicit caller (Telegram
    /// /reset-drawdown command) so a panicked operator cannot bypass the freeze with a
    /// silent in-process toggle. Logs as a manual override for the audit trail.
    /// </summary>
    public void ManualReset(string operatorTag, string postMortemUrl)
    {
        lock (_lock)
        {
            _logger.LogWarning(
                "DrawdownLadderGate MANUAL RESET by {Op}. Was tier {Tier} entered at {Ts:o}. Post-mortem: {Url}",
                operatorTag, _currentTier, _tierEnteredUtc, postMortemUrl);
            _currentTier = DrawdownTier.None;
            _tierEnteredUtc = default;
            _autoResetEligibleUtc = DateTime.MaxValue;
            _lastTriggerNote = $"Manual reset by {operatorTag} at {DateTime.UtcNow:o}. Post-mortem: {postMortemUrl}";
        }
    }

    private decimal _cachedEquityForSnapshot = 0m;
    private decimal GetCurrentEquityFromCache() => _cachedEquityForSnapshot;

    private void UpdateTierState(decimal currentEquity)
    {
        _cachedEquityForSnapshot = currentEquity;
        if (_basis <= 0m) return;
        if (currentEquity > _peakEquity) _peakEquity = currentEquity;
        var drawdownPct = (_peakEquity - currentEquity) / _basis;
        var now = DateTime.UtcNow;

        // Auto-reset eligibility: drawdown recovered above (negative of) recovery threshold.
        if (_currentTier != DrawdownTier.None && drawdownPct < _autoResetRecoveryPct)
        {
            if (_autoResetEligibleUtc == DateTime.MaxValue)
                _autoResetEligibleUtc = now + TimeSpan.FromHours(2);
            else if (now >= _autoResetEligibleUtc && _currentTier != DrawdownTier.Tier3)
            {
                _logger.LogInformation(
                    "DrawdownLadderGate auto-reset: drawdown recovered to {Pct:P2} (< {Reset:P2}) for ≥2h. Was tier {Tier}.",
                    drawdownPct, _autoResetRecoveryPct, _currentTier);
                _currentTier = DrawdownTier.None;
                _tierEnteredUtc = default;
                _autoResetEligibleUtc = DateTime.MaxValue;
                return;
            }
        }
        else if (drawdownPct >= _autoResetRecoveryPct)
        {
            _autoResetEligibleUtc = DateTime.MaxValue; // recovery interrupted; reset clock
        }

        // Tier upgrades (one-way unless manual reset for tier 3, or auto-reset for tier 1/2).
        DrawdownTier newTier = _currentTier;
        if (drawdownPct >= _tier3Pct) newTier = DrawdownTier.Tier3;
        else if (drawdownPct >= _tier2Pct && _currentTier != DrawdownTier.Tier3) newTier = DrawdownTier.Tier2;
        else if (drawdownPct >= _tier1Pct && _currentTier == DrawdownTier.None) newTier = DrawdownTier.Tier1;

        // Auto-expire tier 1 / 2 by elapsed freeze duration (only if drawdown still in tier 1/2 band).
        if (_currentTier == DrawdownTier.Tier1 && now - _tierEnteredUtc >= _tier1FreezeDuration && drawdownPct < _tier1Pct)
            newTier = DrawdownTier.None;
        if (_currentTier == DrawdownTier.Tier2 && now - _tierEnteredUtc >= _tier2FreezeDuration && drawdownPct < _tier2Pct)
            newTier = DrawdownTier.None;

        if (newTier != _currentTier)
        {
            var oldTier = _currentTier;
            _currentTier = newTier;
            _tierEnteredUtc = now;
            _lastTriggerNote = $"{oldTier}→{newTier} at drawdown {drawdownPct:P2} ({_peakEquity:F2}→{currentEquity:F2})";
            if (newTier > oldTier)
            {
                var severity = newTier == DrawdownTier.Tier3 ? AlertSeverity.Critical : AlertSeverity.Warning;
                _logger.LogCritical(
                    "DrawdownLadderGate TIER ESCALATED to {Tier}. Drawdown {Pct:P2} of basis ${Basis:N2}. {Note}",
                    newTier, drawdownPct, _basis, _lastTriggerNote);
                _ = _alerter?.SendAsync($"drawdown_ladder_{newTier}", severity,
                    $"Drawdown Ladder → {newTier}",
                    $"Equity dropped to {drawdownPct:P2} of basis ${_basis:N2}.\n" +
                    $"Peak ${_peakEquity:N2} → Current ${currentEquity:N2}.\n" +
                    (newTier == DrawdownTier.Tier3
                        ? "Indefinite entry freeze. Write post-mortem and run /reset-drawdown to clear."
                        : $"Entry freeze {(newTier == DrawdownTier.Tier1 ? _tier1FreezeDuration : _tier2FreezeDuration).TotalHours:F0}h."),
                    CancellationToken.None);
            }
        }
    }

    private static decimal ReadDecimal(IConfiguration cfg, string key, decimal fallback)
        => decimal.TryParse(cfg[key], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0m ? v : fallback;
    private static double ReadDouble(IConfiguration cfg, string key, double fallback)
        => double.TryParse(cfg[key], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}

public enum DrawdownTier { None = 0, Tier1 = 1, Tier2 = 2, Tier3 = 3 }

public sealed record DrawdownLadderStatus(
    decimal Basis,
    decimal PeakEquity,
    decimal CurrentDrawdownPct,
    DrawdownTier ActiveTier,
    DateTime TierEnteredUtc,
    DateTime AutoResetEligibleUtc,
    string LastTriggerNote);
