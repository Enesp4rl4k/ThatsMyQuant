using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Orchestrates model retraining with three independent triggers:
///   1. Schedule  — always retrain every N hours (cadence-based freshness).
///   2. Drift     — retrain when FeatureDriftMonitor reports distribution shift.
///   3. Decay     — retrain when live IC/AUC falls below a floor (not implemented here;
///                  hook via <see cref="MarkPerformanceDecay"/>).
///
/// Prevents two pathologies common in retail bots:
///   • Model rot (trained once, forever stale).
///   • Over-retraining (retrains on noise, flaps between weights).
///
/// Thread-safe: retrain decisions serialised on a semaphore so concurrent tick
/// handlers can't fire overlapping retrains.
/// </summary>
public sealed class ModelRetrainOrchestrator
{
    private readonly ILogger<ModelRetrainOrchestrator> _logger;
    private readonly ModelTrainingPipeline _trainingPipeline;
    private readonly FeatureDriftMonitor _driftMonitor;
    private readonly RetrainPolicy _policy;
    private readonly SemaphoreSlim _retrainGate = new(1, 1);

    private DateTime _lastRetrainUtc = DateTime.MinValue;
    private int _retrainCount;
    private bool _performanceDecayFlagged;

    public ModelRetrainOrchestrator(
        ILogger<ModelRetrainOrchestrator> logger,
        ModelTrainingPipeline trainingPipeline,
        FeatureDriftMonitor driftMonitor,
        RetrainPolicy? policy = null)
    {
        _logger = logger;
        _trainingPipeline = trainingPipeline;
        _driftMonitor = driftMonitor;
        _policy = policy ?? RetrainPolicy.Default;
    }

    public int RetrainCount => _retrainCount;
    public DateTime LastRetrainUtc => _lastRetrainUtc;

    /// <summary>Caller (e.g. strategy performance monitor) sets this when live IC/AUC
    /// decays below the acceptable floor. Retrain orchestrator picks it up on next
    /// evaluation.</summary>
    public void MarkPerformanceDecay() => _performanceDecayFlagged = true;

    /// <summary>Evaluate all triggers and retrain if any fires. Returns true when a
    /// retrain was attempted (whether or not the training itself succeeded).</summary>
    public async Task<RetrainEvaluation> EvaluateAndRetrainAsync(
        DriftReport? latestDrift = null,
        Func<Task<TrainingOutcome>>? trainCallback = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var reasons = new List<string>();

        bool stale = (now - _lastRetrainUtc) >= _policy.ScheduledInterval;
        if (stale && _lastRetrainUtc != DateTime.MinValue) reasons.Add("Scheduled");

        if (latestDrift is { DriftedCount: > 0 }) reasons.Add($"Drift({latestDrift.DriftedCount})");
        if (_performanceDecayFlagged) reasons.Add("PerformanceDecay");

        // Cooldown: never retrain more than once per MinInterval no matter how many
        // triggers fire. Protects the model from flapping during regime turbulence.
        bool coolingDown = (now - _lastRetrainUtc) < _policy.MinInterval;

        if (reasons.Count == 0 || coolingDown)
        {
            return new RetrainEvaluation(
                Fired: false,
                Reasons: reasons,
                CoolingDown: coolingDown,
                Outcome: null);
        }

        await _retrainGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Model retrain firing | triggers: {Reasons}", string.Join(",", reasons));
            TrainingOutcome outcome;
            if (trainCallback is not null)
            {
                outcome = await trainCallback().ConfigureAwait(false);
            }
            else
            {
                outcome = new TrainingOutcome(Success: false, Message: "No train callback supplied");
            }

            _lastRetrainUtc = now;
            _retrainCount++;
            _performanceDecayFlagged = false;

            return new RetrainEvaluation(
                Fired: true,
                Reasons: reasons,
                CoolingDown: false,
                Outcome: outcome);
        }
        finally
        {
            _retrainGate.Release();
        }
    }
}

/// <summary>Retrain scheduling policy. Cooldown >= MinInterval prevents thrash.</summary>
public sealed record RetrainPolicy(
    TimeSpan ScheduledInterval,
    TimeSpan MinInterval)
{
    public static RetrainPolicy Default => new(
        ScheduledInterval: TimeSpan.FromHours(24),
        MinInterval: TimeSpan.FromHours(2));
}

public sealed record RetrainEvaluation(
    bool Fired,
    List<string> Reasons,
    bool CoolingDown,
    TrainingOutcome? Outcome);

public sealed record TrainingOutcome(bool Success, string Message, double? NewIc = null, double? NewAuc = null);
