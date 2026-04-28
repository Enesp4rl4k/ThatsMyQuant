using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.Strategy.ProMode;

/// <summary>
/// Candidate-pool state machine. Tracks opportunities through:
///   Observed (1st signal) → Watching (7-day OOS window) → Promoted (ready for live)
///                                                       → Rejected (OOS failed)
///
/// Promotion gate requirements:
///   - At least <see cref="MinObservations"/> signals during the watching window
///   - Simulated PF ≥ <see cref="MinProfitFactor"/>
///   - Simulated Sharpe ≥ <see cref="MinSharpe"/>
///
/// This is a metadata tracker ONLY — actual OOS simulation is done elsewhere
/// (via a backtest on the symbol with config applied). Caller invokes
/// <see cref="RecordOosValidation"/> to feed back the simulated result.
/// </summary>
public sealed class CandidatePool
{
    private readonly ILogger<CandidatePool> _logger;
    private readonly ConcurrentDictionary<string, PromotionCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan ObservationWindow { get; init; } = TimeSpan.FromDays(7);
    public int MinObservations { get; init; } = 5;
    public double MinProfitFactor { get; init; } = 1.15;
    public double MinSharpe { get; init; } = 1.0;

    public CandidatePool(ILogger<CandidatePool> logger) { _logger = logger; }

    public IReadOnlyCollection<PromotionCandidate> Snapshot() => _candidates.Values.ToArray();

    /// <summary>Record a new opportunity signal. Creates or updates the candidate.</summary>
    public void RecordSignal(string symbol, string strategyTag, DateTime nowUtc)
    {
        _candidates.AddOrUpdate(symbol,
            _ => new PromotionCandidate(symbol, strategyTag, nowUtc),
            (_, existing) =>
            {
                existing.ObservationCount++;
                existing.LastSignalUtc = nowUtc;
                return existing;
            });
    }

    /// <summary>Feed the OOS backtest outcome into the candidate. Called by the orchestrator.</summary>
    public PromotionDecision RecordOosValidation(string symbol, double oosSharpe, double oosProfitFactor, int oosTrades)
    {
        if (!_candidates.TryGetValue(symbol, out var cand))
            return new PromotionDecision(symbol, false, "Unknown symbol", 0, 0);

        cand.OosSharpe = oosSharpe;
        cand.OosProfitFactor = oosProfitFactor;
        cand.OosTrades = oosTrades;
        cand.OosValidatedUtc = DateTime.UtcNow;

        bool passed = oosTrades >= 10
                      && oosProfitFactor >= MinProfitFactor
                      && oosSharpe >= MinSharpe;

        cand.State = passed ? CandidateState.Promoted : CandidateState.Rejected;
        cand.RejectionReason = passed ? "" :
            $"OOS failed: trades={oosTrades} (need≥10), PF={oosProfitFactor:F2} (need≥{MinProfitFactor:F2}), Sharpe={oosSharpe:F2} (need≥{MinSharpe:F2})";

        _logger.LogInformation("Candidate {Sym} {State}: PF={PF:F2} Sharpe={S:F2} trades={T}",
            symbol, cand.State, oosProfitFactor, oosSharpe, oosTrades);

        return new PromotionDecision(symbol, passed, cand.RejectionReason, oosSharpe, oosProfitFactor);
    }

    /// <summary>Periodic maintenance: move Observed → Watching, prune stale.</summary>
    public void Maintain(DateTime nowUtc)
    {
        foreach (var cand in _candidates.Values)
        {
            if (cand.State == CandidateState.Observed && cand.ObservationCount >= MinObservations)
            {
                cand.State = CandidateState.Watching;
                cand.WatchingStartedUtc = nowUtc;
            }

            if (cand.State == CandidateState.Watching &&
                nowUtc - cand.WatchingStartedUtc > ObservationWindow &&
                cand.OosValidatedUtc == default)
            {
                cand.State = CandidateState.Rejected;
                cand.RejectionReason = $"Watching window expired without OOS validation ({ObservationWindow.TotalDays}d).";
            }
        }
    }
}

public sealed class PromotionCandidate
{
    public string Symbol { get; }
    public string StrategyTag { get; }
    public DateTime FirstSignalUtc { get; }
    public DateTime LastSignalUtc { get; set; }
    public int ObservationCount { get; set; } = 1;
    public DateTime WatchingStartedUtc { get; set; }
    public CandidateState State { get; set; } = CandidateState.Observed;

    public double OosSharpe { get; set; }
    public double OosProfitFactor { get; set; }
    public int OosTrades { get; set; }
    public DateTime OosValidatedUtc { get; set; }
    public string RejectionReason { get; set; } = "";

    public PromotionCandidate(string symbol, string strategyTag, DateTime nowUtc)
    {
        Symbol = symbol;
        StrategyTag = strategyTag;
        FirstSignalUtc = nowUtc;
        LastSignalUtc = nowUtc;
    }
}

public enum CandidateState { Observed, Watching, Promoted, Rejected }

public sealed record PromotionDecision(
    string Symbol,
    bool Approved,
    string Reason,
    double Sharpe,
    double ProfitFactor);
