using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Background timer that wakes periodically and asks the <see cref="ModelRetrainOrchestrator"/>
/// whether a retrain is warranted (scheduled cadence, drift, or flagged decay). The
/// orchestrator itself enforces cooldown, so over-ticking this timer is cheap.
/// </summary>
public sealed class ModelRetrainHostedService : BackgroundService
{
    private readonly ILogger<ModelRetrainHostedService> _logger;
    private readonly ModelRetrainOrchestrator _orchestrator;
    private readonly FeatureDriftMonitor _driftMonitor;
    private readonly ModelTrainingPipeline _trainingPipeline;
    private readonly TimeSpan _pollInterval;

    public ModelRetrainHostedService(
        ILogger<ModelRetrainHostedService> logger,
        ModelRetrainOrchestrator orchestrator,
        FeatureDriftMonitor driftMonitor,
        ModelTrainingPipeline trainingPipeline,
        TimeSpan? pollInterval = null)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _driftMonitor = driftMonitor;
        _trainingPipeline = trainingPipeline;
        _pollInterval = pollInterval ?? TimeSpan.FromMinutes(15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ModelRetrainHostedService started (poll={Interval})", _pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var eval = await _orchestrator.EvaluateAndRetrainAsync(
                    latestDrift: null, // caller may swap in a drift snapshot source later
                    trainCallback: RunTrainingAsync,
                    ct: stoppingToken).ConfigureAwait(false);

                if (eval.Fired)
                {
                    _logger.LogInformation(
                        "Retrain fired | reasons={Reasons} outcome={Success} msg={Msg}",
                        string.Join(",", eval.Reasons),
                        eval.Outcome?.Success,
                        eval.Outcome?.Message);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModelRetrainHostedService loop error — continuing");
            }

            try { await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        _logger.LogInformation("ModelRetrainHostedService stopped");
    }

    /// <summary>
    /// Calls the real ModelTrainingPipeline.Train() on the CPU thread pool. Maps
    /// TrainingResult → TrainingOutcome; success path returns accuracy as proxy IC
    /// and balanced accuracy as proxy AUC (informational — not used for gating here).
    /// </summary>
    private Task<TrainingOutcome> RunTrainingAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var result = _trainingPipeline.Train(
                    trainSplitRatio: 0.8,
                    epochs: 250,
                    learningRate: 0.05,
                    persistArtifact: true);
                if (!result.Success)
                {
                    return new TrainingOutcome(false, $"Training skipped: {result.Reason}");
                }
                _logger.LogInformation(
                    "Retrain OK | train={Train} test={Test} acc={Acc:F3} bal={Bal:F3} logLoss={LL:F3}",
                    result.TrainSamples, result.TestSamples, result.Accuracy, result.BalancedAccuracy, result.LogLoss);
                return new TrainingOutcome(
                    Success: true,
                    Message: $"train={result.TrainSamples} test={result.TestSamples} acc={result.Accuracy:F3} bal={result.BalancedAccuracy:F3}",
                    NewIc: result.BalancedAccuracy,
                    NewAuc: result.Accuracy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retrain training invocation threw");
                return new TrainingOutcome(false, ex.Message);
            }
        });
    }
}
