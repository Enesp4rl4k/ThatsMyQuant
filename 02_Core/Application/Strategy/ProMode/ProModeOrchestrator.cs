using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Level 2.5 "Pro Mode" orchestrator. Wires together the four safety + discovery
/// building blocks into a single coherent service:
///
///   • RegimeLeverageThrottle   — reduces leverage in Volatile regime
///   • CircuitBreakerMonitor    — pauses all trading at 5% portfolio DD
///   • FundingOpportunityScanner — flags candidate signals across a watchlist
///   • CandidatePool            — 7-day OOS gate before promotion
///
/// Phase A scope: MONITORING + ALERT ONLY. Orchestrator emits snapshots for the UI;
/// actual execution is gated by operator approval in the Pro Mode tab. Later phases
/// (B/C) will auto-route promoted candidates to the portfolio.
/// </summary>
public sealed class ProModeOrchestrator
{
    private readonly ILogger<ProModeOrchestrator> _logger;
    private readonly FundingOpportunityScanner _fundingScanner;
    private readonly CandidatePool _candidatePool;
    private readonly CircuitBreakerMonitor _circuitBreaker;

    private readonly object _lock = new();
    private bool _isArmed;
    private MarketRegime _currentRegime = MarketRegime.Unknown;
    private decimal _baseLeverage = 1.0m;
    private decimal _effectiveLeverage = 1.0m;
    private readonly List<FundingOpportunity> _recentOpportunities = new();
    private const int RecentOpportunityCap = 100;

    public ProModeOrchestrator(
        ILogger<ProModeOrchestrator> logger,
        FundingOpportunityScanner fundingScanner,
        CandidatePool candidatePool,
        CircuitBreakerMonitor circuitBreaker)
    {
        _logger = logger;
        _fundingScanner = fundingScanner;
        _candidatePool = candidatePool;
        _circuitBreaker = circuitBreaker;
    }

    public bool IsArmed
    {
        get { lock (_lock) return _isArmed; }
    }

    public ProModeSnapshot Snapshot()
    {
        lock (_lock)
            return new ProModeSnapshot(
                IsArmed: _isArmed,
                CircuitBreaker: _circuitBreaker.Snapshot(),
                CurrentRegime: _currentRegime,
                BaseLeverage: _baseLeverage,
                EffectiveLeverage: _effectiveLeverage,
                LeverageExplanation: RegimeLeverageThrottle.Explain(_baseLeverage, _currentRegime, _effectiveLeverage),
                RecentOpportunities: _recentOpportunities.ToArray(),
                CandidatePool: _candidatePool.Snapshot());
    }

    public void Arm(decimal baseLeverage)
    {
        lock (_lock)
        {
            _isArmed = true;
            _baseLeverage = baseLeverage;
            _effectiveLeverage = RegimeLeverageThrottle.Compute(baseLeverage, _currentRegime);
            _logger.LogWarning("Pro Mode ARMED. Base leverage {Base:F1}x → effective {Eff:F1}x (regime: {Regime})",
                baseLeverage, _effectiveLeverage, _currentRegime);
        }
    }

    public void Disarm(string operatorNote)
    {
        lock (_lock)
        {
            _isArmed = false;
            _logger.LogWarning("Pro Mode DISARMED by operator. Note: {Note}", operatorNote);
        }
    }

    /// <summary>Operator-issued kill switch — disarms AND disables circuit breaker trading
    /// (resolves via <see cref="CircuitBreakerMonitor.IsTradingAllowed"/> → false).</summary>
    public void EmergencyKill(string operatorNote)
    {
        lock (_lock)
        {
            _isArmed = false;
            _logger.LogError("EMERGENCY KILL SWITCH TRIGGERED by operator. Note: {Note}. All new trades blocked.", operatorNote);
        }
    }

    /// <summary>Update the current regime (from RegimeDetector). Recomputes effective leverage.</summary>
    public void UpdateRegime(MarketRegime regime)
    {
        lock (_lock)
        {
            _currentRegime = regime;
            _effectiveLeverage = RegimeLeverageThrottle.Compute(_baseLeverage, regime);
        }
    }

    /// <summary>Feed latest portfolio equity to the circuit breaker.</summary>
    public void UpdateEquity(decimal equity) => _circuitBreaker.UpdateEquity(equity);

    /// <summary>Returns true if a new entry is allowed RIGHT NOW (armed + breaker not tripped).</summary>
    public bool IsEntryAllowed()
    {
        lock (_lock)
            return _isArmed && _circuitBreaker.IsTradingAllowed();
    }

    /// <summary>One scan cycle — invoked by a DispatcherTimer / hosted service every 30-60s.</summary>
    public IReadOnlyList<FundingOpportunity> ScanCycle(DateTime nowUtc)
    {
        var opps = _fundingScanner.ScanOnce(nowUtc);
        lock (_lock)
        {
            foreach (var o in opps)
            {
                _recentOpportunities.Insert(0, o);
                _candidatePool.RecordSignal(o.Symbol, strategyTag: "FundingAlpha", nowUtc);
            }
            if (_recentOpportunities.Count > RecentOpportunityCap)
                _recentOpportunities.RemoveRange(RecentOpportunityCap, _recentOpportunities.Count - RecentOpportunityCap);
        }
        _candidatePool.Maintain(nowUtc);
        return opps;
    }

    public PromotionDecision ValidateCandidateOos(string symbol, double oosSharpe, double oosPf, int oosTrades)
        => _candidatePool.RecordOosValidation(symbol, oosSharpe, oosPf, oosTrades);
}

public sealed record ProModeSnapshot(
    bool IsArmed,
    CircuitBreakerSnapshot CircuitBreaker,
    MarketRegime CurrentRegime,
    decimal BaseLeverage,
    decimal EffectiveLeverage,
    string LeverageExplanation,
    IReadOnlyList<FundingOpportunity> RecentOpportunities,
    IReadOnlyCollection<PromotionCandidate> CandidatePool);
