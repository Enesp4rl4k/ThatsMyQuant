using System.Collections.Concurrent;
using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Notifications;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Champion/Challenger deployment pattern. A new (challenger) strategy version runs
/// in *shadow* mode alongside the live (champion): both generate signals from the
/// same market state, but only the champion's signals reach the execution pipeline.
/// The challenger's decisions are recorded and its hypothetical PnL accumulated.
///
/// After N decisions (or T days), this service's <see cref="TryPromote"/> checks
/// whether the challenger beats the champion on a pre-committed statistical test:
///   • Sharpe uplift ≥ threshold AND
///   • Hypothetical-vs-champion paired PnL t-stat ≥ minSignificance (default 2.0)
/// Only then does the challenger become the new champion.
///
/// Prevents two common failures: (a) deploying a strategy that backtests well but
/// loses in live noise, and (b) flapping between strategies on short-window luck.
/// </summary>
public sealed class ChampionChallengerService : INotificationHandler<PositionClosedNotification>
{
    private readonly ILogger<ChampionChallengerService> _logger;
    private readonly ConcurrentDictionary<string, ChallengerState> _challengers = new();

    public ChampionChallengerService(ILogger<ChampionChallengerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// MediatR subscriber: on every PositionClosedNotification, if a challenger is
    /// registered for the strategy, pull its hypothetical PnL via registered adapter
    /// and record the paired outcome. When no adapter is configured we still store the
    /// champion PnL so future paired evaluators can align by index.
    /// </summary>
    public Task Handle(PositionClosedNotification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.StrategyTag))
            return Task.CompletedTask;

        if (!_challengers.TryGetValue(notification.StrategyTag, out var state))
            return Task.CompletedTask;

        // Without a paired-strategy adapter we record the champion outcome only; the
        // challenger side is supplied either via RecordPairedOutcome() from a shadow
        // evaluator, or by assigning PairedChallengerPnlEstimator before the trade closes.
        var hypotheticalChallengerPnl = state.PairedChallengerPnlEstimator?.Invoke(notification)
            ?? notification.NetPnl;  // no adapter = flat paired (uplift=0, won't promote)
        state.Record(notification.NetPnl, hypotheticalChallengerPnl);
        return Task.CompletedTask;
    }

    public void RegisterChallenger(string strategyTag, string version)
    {
        _challengers[strategyTag] = new ChallengerState(strategyTag, version);
        _logger.LogInformation("Challenger {Tag} {Version} registered in shadow mode", strategyTag, version);
    }

    /// <summary>Record a paired decision: both champion and challenger evaluated the
    /// same signal and each proposed (or rejected) a trade. Store the outcome PnL once
    /// known so promotion can evaluate.</summary>
    public void RecordPairedOutcome(string strategyTag, decimal championPnl, decimal challengerPnl)
    {
        if (!_challengers.TryGetValue(strategyTag, out var state)) return;
        state.Record(championPnl, challengerPnl);
    }

    /// <summary>
    /// Paired t-test on (challenger - champion) PnL differences. When the mean
    /// difference is positive AND |t| ≥ minSignificance AND sample size ≥ minSamples,
    /// the challenger is promoted (returns true). Champion's prior state is cleared.
    /// </summary>
    public PromotionAttempt TryPromote(
        string strategyTag,
        int minSamples = 30,
        double minSignificance = 2.0,
        double minSharpeUplift = 0.2)
    {
        if (!_challengers.TryGetValue(strategyTag, out var state))
            return new PromotionAttempt(false, "No challenger registered", 0, 0, 0);

        var diffs = state.SnapshotDifferences();
        if (diffs.Length < minSamples)
            return new PromotionAttempt(false, $"Sample too small ({diffs.Length} < {minSamples})", 0, 0, diffs.Length);

        double mean = 0.0;
        for (int i = 0; i < diffs.Length; i++) mean += diffs[i];
        mean /= diffs.Length;

        double var = 0.0;
        for (int i = 0; i < diffs.Length; i++) { double d = diffs[i] - mean; var += d * d; }
        var /= Math.Max(1, diffs.Length - 1);
        double std = Math.Sqrt(var);
        double tStat = std > 0 ? mean / (std / Math.Sqrt(diffs.Length)) : 0.0;

        double champSharpe = ComputeSharpeFromPnls(state.ChampionPnls);
        double chalSharpe = ComputeSharpeFromPnls(state.ChallengerPnls);
        double uplift = chalSharpe - champSharpe;

        bool approved = mean > 0 && tStat >= minSignificance && uplift >= minSharpeUplift;
        var reason = approved
            ? $"Promoted: meanΔ={mean:F4} t={tStat:F2} sharpeUplift={uplift:F2}"
            : $"Rejected: meanΔ={mean:F4} t={tStat:F2} sharpeUplift={uplift:F2} (need t≥{minSignificance} uplift≥{minSharpeUplift})";

        if (approved)
        {
            _logger.LogInformation("Champion/Challenger PROMOTION {Tag}: {Reason}", strategyTag, reason);
            _challengers.TryRemove(strategyTag, out _);
        }
        else
        {
            _logger.LogInformation("Champion/Challenger kept {Tag}: {Reason}", strategyTag, reason);
        }

        return new PromotionAttempt(approved, reason, tStat, uplift, diffs.Length);
    }

    public ChallengerState? GetState(string strategyTag)
        => _challengers.TryGetValue(strategyTag, out var s) ? s : null;

    private static double ComputeSharpeFromPnls(IReadOnlyList<decimal> pnls)
    {
        if (pnls.Count < 2) return 0.0;
        double mean = 0.0;
        for (int i = 0; i < pnls.Count; i++) mean += (double)pnls[i];
        mean /= pnls.Count;
        double var = 0.0;
        for (int i = 0; i < pnls.Count; i++) { double d = (double)pnls[i] - mean; var += d * d; }
        var /= (pnls.Count - 1);
        double std = Math.Sqrt(var);
        return std > 0 ? (mean / std) * Math.Sqrt(252.0) : 0.0;
    }
}

public sealed class ChallengerState
{
    private readonly List<decimal> _championPnls = new();
    private readonly List<decimal> _challengerPnls = new();
    private readonly Lock _lock = new();

    public string StrategyTag { get; }
    public string Version { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public IReadOnlyList<decimal> ChampionPnls { get { lock (_lock) return _championPnls.ToArray(); } }
    public IReadOnlyList<decimal> ChallengerPnls { get { lock (_lock) return _challengerPnls.ToArray(); } }

    /// <summary>
    /// Optional adapter that maps a closed champion trade to the challenger's hypothetical
    /// PnL. Set by whoever registers the challenger — typically a shadow signal evaluator
    /// that replays the same market state through the challenger's strategy.
    /// </summary>
    public Func<PositionClosedNotification, decimal>? PairedChallengerPnlEstimator { get; set; }

    public ChallengerState(string strategyTag, string version)
    {
        StrategyTag = strategyTag;
        Version = version;
    }

    public void Record(decimal championPnl, decimal challengerPnl)
    {
        lock (_lock)
        {
            _championPnls.Add(championPnl);
            _challengerPnls.Add(challengerPnl);
        }
    }

    public double[] SnapshotDifferences()
    {
        lock (_lock)
        {
            var diffs = new double[_championPnls.Count];
            for (int i = 0; i < diffs.Length; i++)
                diffs[i] = (double)(_challengerPnls[i] - _championPnls[i]);
            return diffs;
        }
    }
}

public sealed record PromotionAttempt(
    bool Approved,
    string Reason,
    double TStatistic,
    double SharpeUplift,
    int SampleSize);
