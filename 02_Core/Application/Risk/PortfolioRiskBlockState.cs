namespace ThatsMyQuant.Application.Risk;

/// <summary>
/// 2026-05-07 (Sprint 3.2 — Master Plan v3) — converts PortfolioRiskLive from
/// observational to enforcing.
///
/// Audit finding: <c>PortfolioRiskLiveHostedService</c> historically computed
/// VaR/CVaR every 15min and emitted a Telegram alert when either exceeded the
/// configured thresholds, but the rest of the system kept opening new positions
/// regardless — the engine had a "see no evil" risk leak. The premortem
/// re-graded this as a missing enforcement leg.
///
/// This class is a thread-safe singleton consumed by the entry path of every
/// strategy executor (Funding/Pairs hybrid). When the periodic VaR/CVaR
/// snapshot finds a breach it calls <see cref="Trip"/>; the executor's
/// pre-entry check then refuses to open new positions until the next snapshot
/// either confirms or clears the breach.
///
/// Read-only (does NOT close existing positions). Existing risk decisions stay
/// with the per-position trailing stop / executor close logic.
/// </summary>
public sealed class PortfolioRiskBlockState
{
    private readonly object _lock = new();
    private bool _blockNewEntries;
    private string _reason = string.Empty;
    private DateTime _trippedAtUtc;
    private DateTime _lastUpdateUtc;

    /// <summary>
    /// True if PortfolioRiskLive has flagged a VaR/CVaR breach. Executors should
    /// CALL this from their pre-entry path and SKIP opening if true.
    /// </summary>
    public bool BlockNewEntries
    {
        get { lock (_lock) return _blockNewEntries; }
    }

    public string Reason
    {
        get { lock (_lock) return _reason; }
    }

    public DateTime TrippedAtUtc
    {
        get { lock (_lock) return _trippedAtUtc; }
    }

    public DateTime LastUpdateUtc
    {
        get { lock (_lock) return _lastUpdateUtc; }
    }

    /// <summary>Set by PortfolioRiskLive when VaR/CVaR breach is observed.</summary>
    public void Trip(string reason)
    {
        lock (_lock)
        {
            if (!_blockNewEntries)
            {
                _trippedAtUtc = DateTime.UtcNow;
            }
            _blockNewEntries = true;
            _reason = reason;
            _lastUpdateUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Cleared by PortfolioRiskLive when the next snapshot is back inside thresholds.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _blockNewEntries = false;
            _reason = string.Empty;
            _lastUpdateUtc = DateTime.UtcNow;
        }
    }
}
