using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Bot;

/// <summary>
/// Cross-tier (Long + Short Funding + Pairs) global daily-PnL aggregator and circuit
/// breaker. Each tier's executor reports its realized PnL on every close via
/// <see cref="RecordRealizedPnl"/>. When the AGGREGATED daily realized PnL crosses the
/// configured loss threshold (% of basis), <see cref="IsTripped"/> returns true and
/// every tier blocks new entries until UTC-midnight reset.
///
/// Per-tier circuit breakers still work as a first line of defence — this is the
/// belt-and-suspenders second layer that catches the case where Long -2% + Short -2%
/// = global -4% but neither tier alone breached its 3% local threshold.
/// </summary>
public sealed class GlobalCircuitBreaker
{
    private readonly ILogger<GlobalCircuitBreaker> _logger;
    private readonly decimal _lossPctOfBasis;
    private readonly object _lock = new();
    private decimal _dailyRealizedPnl = 0m;
    private DateTime _resetDateUtc = DateTime.UtcNow.Date;
    private bool _tripped = false;
    private string _trippingTier = string.Empty;

    public GlobalCircuitBreaker(ILogger<GlobalCircuitBreaker> logger, IConfiguration cfg)
    {
        _logger = logger;
        var raw = cfg["Hybrid:GlobalCircuitBreakerDailyLossPct"]
            ?? cfg["Hybrid:CircuitBreakerDailyLossPct"];
        _lossPctOfBasis = decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0m ? v : 0.04m;
        _logger.LogInformation("GlobalCircuitBreaker initialised. Threshold={Pct:P2} of basis.", _lossPctOfBasis);
    }

    /// <summary>Threshold as fraction of basis (e.g. 0.04 = 4%).</summary>
    public decimal Threshold => _lossPctOfBasis;

    /// <summary>Current cumulative realized PnL for today (UTC).</summary>
    public decimal DailyRealizedPnl
    {
        get { lock (_lock) { RollIfNewDay(); return _dailyRealizedPnl; } }
    }

    /// <summary>True when aggregated daily loss exceeds threshold.</summary>
    public bool IsTripped
    {
        get { lock (_lock) { RollIfNewDay(); return _tripped; } }
    }

    /// <summary>Symbolic name of the tier whose close-event TIPPED us over.</summary>
    public string TrippingTier
    {
        get { lock (_lock) { return _trippingTier; } }
    }

    /// <summary>Called by each tier's HandleCloseAsync after netPnl computed.
    /// Aggregates into the day's running total and trips the breaker if threshold breached.</summary>
    /// <returns>True if THIS call tripped the breaker (caller should fire CRITICAL alert).</returns>
    public bool RecordRealizedPnl(string tierLabel, decimal netPnl, decimal basis)
    {
        lock (_lock)
        {
            RollIfNewDay();
            _dailyRealizedPnl += netPnl;
            if (_tripped) return false;
            var lossPct = _dailyRealizedPnl < 0m ? -_dailyRealizedPnl / Math.Max(basis, 1m) : 0m;
            if (lossPct >= _lossPctOfBasis)
            {
                _tripped = true;
                _trippingTier = tierLabel;
                _logger.LogCritical(
                    "GLOBAL CIRCUIT BREAKER TRIPPED via {Tier}. Cumulative daily PnL ${Pnl:N2} = {Pct:P2} of basis ${Basis:N2}. New entries on ALL tiers SUPPRESSED until UTC midnight.",
                    tierLabel, _dailyRealizedPnl, lossPct, basis);
                return true;
            }
            return false;
        }
    }

    /// <summary>Manual reset (e.g. UI control). Logs as a manual override.</summary>
    public void ManualReset(string operatorTag)
    {
        lock (_lock)
        {
            _logger.LogWarning("GlobalCircuitBreaker MANUAL RESET by {Op} (was tripped via {Tier}, daily PnL ${Pnl:N2}).",
                operatorTag, _trippingTier, _dailyRealizedPnl);
            _tripped = false;
            _trippingTier = string.Empty;
        }
    }

    private void RollIfNewDay()
    {
        var todayUtc = DateTime.UtcNow.Date;
        if (todayUtc > _resetDateUtc)
        {
            _logger.LogInformation("GlobalCircuitBreaker daily reset. Yesterday PnL ${Pnl:N2} (was tripped: {Tripped} via {Tier}).",
                _dailyRealizedPnl, _tripped, _trippingTier);
            _resetDateUtc = todayUtc;
            _dailyRealizedPnl = 0m;
            _tripped = false;
            _trippingTier = string.Empty;
        }
    }
}
