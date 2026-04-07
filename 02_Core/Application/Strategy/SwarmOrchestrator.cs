using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Options;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace ThatsMyQuant.Application.Strategy;

public class SwarmNegotiationRequest : IRequest<SwarmConsensusResult>
{
    public Signal Signal { get; set; } = null!;
    public TradeDecision InitialDecision { get; set; } = null!;
    public RiskParameters Risk { get; set; } = null!;
    public IReadOnlyList<Candle> Candles { get; set; } = null!;
    public FoundationSnapshot Foundation { get; set; } = FoundationSnapshot.Empty;
    public PatternSnapshot Patterns { get; set; } = PatternSnapshot.Empty;
    public MarketRegime Regime { get; set; } = MarketRegime.Ranging;
}

public class SwarmConsensusResult
{
    public bool Approved { get; set; }
    public SignalReasonFlags RejectionFlags { get; set; } = SignalReasonFlags.None;
    public ExecutionActionType ExecutionAction { get; set; } = ExecutionActionType.None;
    public decimal AdjustedKellyFraction { get; set; }
    public decimal AdjustedTakeProfit { get; set; }
}

public class SwarmOrchestrator : IRequestHandler<SwarmNegotiationRequest, SwarmConsensusResult>
{
    private readonly RiskAgent _riskAgent;
    private readonly SentimentAgent _sentimentAgent;
    private readonly InferenceAgent _inferenceAgent;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly IPortfolioStateCache _portfolioCache;
    private readonly OFIExecutionAgent _ofiAgent;
    private readonly IAgentCoordinator _agentCoordinator;
    private readonly ILogger<SwarmOrchestrator> _logger;

    private readonly SwarmOptions _swarmOptions;
    private readonly float _mlDownsideThreshold;
    private readonly float _successThreshold;
    private readonly float _elevatedDriftPenalty;
    private readonly decimal _elevatedDriftSizeMultiplier;
    private readonly int _maxConsecutiveFailures;
    private readonly TimeSpan _haltCooldownPeriod;
    private readonly TimeSpan _agentTimeout;

    private int _consecutiveFailures;
    private volatile bool _isSystemHalted;
    private long _haltedAtTicks;

    public SwarmOrchestrator(
        RiskAgent riskAgent,
        SentimentAgent sentimentAgent,
        InferenceAgent inferenceAgent,
        FeatureEngineeringAgent featureAgent,
        IPortfolioStateCache portfolioCache,
        OFIExecutionAgent ofiAgent,
        IAgentCoordinator agentCoordinator,
        ILogger<SwarmOrchestrator> logger,
        IOptions<SwarmOptions>? swarmOptions = null)
    {
        _riskAgent = riskAgent;
        _sentimentAgent = sentimentAgent;
        _inferenceAgent = inferenceAgent;
        _featureAgent = featureAgent;
        _portfolioCache = portfolioCache;
        _ofiAgent = ofiAgent;
        _agentCoordinator = agentCoordinator;
        _logger = logger;
        _swarmOptions = swarmOptions?.Value ?? new SwarmOptions();
        _mlDownsideThreshold = _swarmOptions.MlDownsideThreshold;
        _successThreshold = _swarmOptions.SuccessThreshold;
        _elevatedDriftPenalty = _swarmOptions.ElevatedDriftPenalty;
        _elevatedDriftSizeMultiplier = _swarmOptions.ElevatedDriftSizeMultiplier;
        _maxConsecutiveFailures = _swarmOptions.MaxConsecutiveFailures;
        _haltCooldownPeriod = TimeSpan.FromMinutes(_swarmOptions.HaltCooldownMinutes);
        _agentTimeout = TimeSpan.FromSeconds(_swarmOptions.AgentTimeoutSeconds);
    }

    public async Task<SwarmConsensusResult> Handle(SwarmNegotiationRequest request, CancellationToken cancellationToken)
    {
        if (_isSystemHalted)
        {
            var haltedAt = new DateTime(Interlocked.Read(ref _haltedAtTicks), DateTimeKind.Utc);
            if (DateTime.UtcNow - haltedAt < _haltCooldownPeriod)
            {
                _logger.LogCritical("Swarm Orchestrator is HALTED (cooldown until {ResumeAt:HH:mm:ss} UTC).",
                    haltedAt + _haltCooldownPeriod);
                return new SwarmConsensusResult { Approved = false, RejectionFlags = SignalReasonFlags.SystemError };
            }

            // Auto-recover after cooldown period
            _isSystemHalted = false;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _logger.LogWarning("Swarm Orchestrator auto-recovered after {Cooldown} cooldown.", _haltCooldownPeriod);
        }

        var result = new SwarmConsensusResult { Approved = false };

        try
        {
            var regime = MarketRegimeDetector.Detect(request.Candles);
            var riskParams = ResolveRegimeAdjustedRisk(request.Risk, regime);
            var recentTrades = _portfolioCache.GetRecentTrades();

            var portfolioGuard = await _riskAgent.EvaluatePortfolioGuardrailsAsync(
                recentTrades,
                request.Signal,
                request.InitialDecision.PositionSizePercent,
                riskParams);

            if (!portfolioGuard.Approved)
            {
                result.RejectionFlags |= SignalReasonFlags.RiskVeto;
                return result;
            }

            var (sentimentVote, riskVote, mlPrediction) = await RunConcurrentAgents(request, riskParams, cancellationToken);
            AppendModelMetadata(request.Signal, mlPrediction);

            var strategyVotes = await _agentCoordinator.CollectStrategyVotesAsync(
                request.Signal, request.Patterns, request.Foundation,
                request.Candles, regime.Regime, cancellationToken);

            var metaAdaptation = await _agentCoordinator.RunMetaAdaptationAsync(
                recentTrades, regime.Regime, cancellationToken);

            float confidenceScore = CalculateConfidence(
                sentimentVote, riskVote, mlPrediction, request.InitialDecision,
                strategyVotes, metaAdaptation, result);

            confidenceScore = await ApplyMicrostructureInjection(request.Signal, result, confidenceScore, cancellationToken);

            var riskGauntletResult = await _agentCoordinator.RunRiskGauntletAsync(
                request.Signal, request.InitialDecision, recentTrades, cancellationToken);

            if (!riskGauntletResult.Approved)
            {
                result.RejectionFlags |= riskGauntletResult.VetoFlag;
                _logger.LogWarning("RiskGauntlet vetoed {Symbol}: {Reason}", request.Signal.Symbol, riskGauntletResult.Reason);
                return result;
            }

            FinalizeConsensus(confidenceScore, riskVote, mlPrediction, request.InitialDecision,
                riskGauntletResult.SizeMultiplier, result);

            // 🔥 Reset failure counter on successful end-to-end execution
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swarm Orchestration CRITICAL FAILURE for {Symbol}", request.Signal.Symbol);
            
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures >= _maxConsecutiveFailures)
            {
                TriggerEmergencyShutdown();
            }

            result.Approved = false;
            result.RejectionFlags |= SignalReasonFlags.SystemError;
        }

        return result;
    }

    private void TriggerEmergencyShutdown()
    {
        _isSystemHalted = true;
        Interlocked.Exchange(ref _haltedAtTicks, DateTime.UtcNow.Ticks);
        _logger.LogCritical("!!! EMERGENCY SHUTDOWN TRIGGERED !!! 3 consecutive orchestration failures. Auto-recovery in {Cooldown}.",
            _haltCooldownPeriod);

        // Arm the RiskAgent's kill switch as a secondary safety layer.
        _riskAgent.ArmKillSwitch("Global Orchestration Failure Threshold Reached (3 Strikes).");
    }

    private RiskParameters ResolveRegimeAdjustedRisk(RiskParameters current, MarketRegimeDetector.RegimeResult regime)
    {
        if (current != RiskParameters.Default) return current;
        return regime.Regime switch
        {
            MarketRegime.Volatile => RiskParameters.Default with { MaxPositionSizePercent = 2.5m },
            MarketRegime.Trending => RiskParameters.Default with { MaxPositionSizePercent = 7.5m },
            _ => RiskParameters.Default
        };
    }

    private async Task<T> RunWithTimeoutAsync<T>(string agentName, Task<T> agentTask, T fallback, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_agentTimeout);
            return await agentTask.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Agent [{AgentName}] timed out after {Timeout}s — using fallback result",
                agentName, _agentTimeout.TotalSeconds);
            return fallback;
        }
    }

    private async Task<((bool Approved, string Reason) Sentiment, (bool Approved, string Reason, decimal SafePositionSizePercent) Risk, PredictionResult Prediction)>
        RunConcurrentAgents(SwarmNegotiationRequest request, RiskParameters riskParams, CancellationToken ct)
    {
        var recentTrades = _portfolioCache.GetRecentTrades();
        var sentimentTask = RunWithTimeoutAsync(
            "Sentiment",
            _sentimentAgent.EvaluateAsync(request.Signal.Symbol, ct),
            (Approved: false, Reason: "Sentiment agent timed out"),
            ct);

        var riskTask = RunWithTimeoutAsync(
            "Risk",
            _riskAgent.EvaluateAsync(
                request.InitialDecision,
                riskParams,
                request.Signal.Volume24h,
                recentTrades,
                request.Signal),
            (Approved: false, Reason: "Risk agent timed out", SafePositionSizePercent: 0m),
            ct);

        // Offload ML latency to avoid blocking I/O pipeline during multi-agent consensus
        var mlTask = RunWithTimeoutAsync(
            "MLInference",
            Task.Run(() =>
            {
                _featureAgent.ApplyFoundationData(request.Foundation);
                return _inferenceAgent.PredictNextReturn(_featureAgent);
            }, ct),
            PredictionResult.Invalid,
            ct);

        await Task.WhenAll(sentimentTask, riskTask, mlTask).WaitAsync(ct);
        return (await sentimentTask, await riskTask, await mlTask);
    }

    private float CalculateConfidence(
        (bool Approved, string Reason) sentiment,
        (bool Approved, string Reason, decimal Size) risk,
        PredictionResult ml,
        TradeDecision decision,
        IReadOnlyList<StrategyVote> strategyVotes,
        MetaAdaptation meta,
        SwarmConsensusResult result)
    {
        float score = 0;
        if (sentiment.Approved) score += 0.3f; else result.RejectionFlags |= SignalReasonFlags.SentimentVeto;
        if (risk.Approved) score += 0.4f; else result.RejectionFlags |= SignalReasonFlags.RiskVeto;

        if (ml.DriftIsCritical)
        {
            result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
            _logger.LogWarning(
                "Critical ML feature drift vetoed {DriftedCount} features (max z-score {MaxZ:F2})",
                ml.DriftedFeatureCount,
                ml.MaxFeatureZScore);
            score -= 0.35f;
        }
        if (ml.IsValid)
        {
            if (ml.Value < _mlDownsideThreshold && decision.Side == TradeSide.Buy)
            {
                result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
                score -= 0.5f;
            }
            else
            {
                score += ml.DriftDetected ? 0.15f : 0.3f;

                if (ml.DriftDetected)
                {
                    _logger.LogInformation(
                        "Elevated ML feature drift reduced model confidence contribution for {DriftedCount} features (max z-score {MaxZ:F2})",
                        ml.DriftedFeatureCount,
                        ml.MaxFeatureZScore);
                    score -= _elevatedDriftPenalty;
                }
            }
        }
        else if (ml.DriftDetected)
        {
            result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
        }

        if (strategyVotes.Count > 0)
        {
            var voteScore = ComputeWeightedVoteScore(strategyVotes, meta);
            score += voteScore * 0.3f;
            _logger.LogDebug("Strategy votes contribution: {Score:F3} ({Count} votes)", voteScore, strategyVotes.Count);
        }

        score *= meta.ConfidenceScalar;

        return score;
    }

    private static float ComputeWeightedVoteScore(IReadOnlyList<StrategyVote> votes, MetaAdaptation meta)
    {
        float weightedSum = 0f, totalWeight = 0f;

        for (var i = 0; i < votes.Count; i++)
        {
            var vote = votes[i];
            if (vote.Vote == VoteOutcome.Abstain) continue;

            var baseWeight = 1.0f;
            if (meta.StrategyWeights.TryGetValue(vote.StrategyTag, out var metaWeight))
                baseWeight = metaWeight;

            var direction = vote.Vote == VoteOutcome.Approve ? 1f : -1f;
            weightedSum += direction * Math.Abs(vote.ConfidenceAdjustment) * baseWeight;
            totalWeight += Math.Abs(vote.ConfidenceAdjustment) * baseWeight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : 0f;
    }

    private async Task<float> ApplyMicrostructureInjection(Signal signal, SwarmConsensusResult result, float confidence, CancellationToken ct)
    {
        if (signal.OrderFlowImbalance.HasValue)
        {
            await _ofiAgent.Handle(signal, ct);
            result.ExecutionAction = signal.ExecutionAction;
            if (result.ExecutionAction != ExecutionActionType.MarketTaker) return confidence + 0.1f;
        }
        return confidence;
    }

    private void FinalizeConsensus(float score, (bool Approved, string Reason, decimal Size) risk, PredictionResult ml,
        TradeDecision decision, decimal sizeMultiplier, SwarmConsensusResult result)
    {
        if (score >= _successThreshold && result.RejectionFlags == SignalReasonFlags.None)
        {
            result.Approved = true;
            var driftAdjustedSizeMultiplier = ml.DriftDetected ? sizeMultiplier * _elevatedDriftSizeMultiplier : sizeMultiplier;
            result.AdjustedKellyFraction = risk.Size * (decimal)(1.0f + ml.Value) * driftAdjustedSizeMultiplier;
            result.AdjustedTakeProfit = decision.TakeProfit;
            if (result.ExecutionAction == ExecutionActionType.None) result.ExecutionAction = ExecutionActionType.MarketTaker;
        }
    }

    private static void AppendModelMetadata(Signal signal, PredictionResult mlPrediction)
    {
        if (!string.IsNullOrWhiteSpace(mlPrediction.ModelVersion))
        {
            signal.Metadata["MlModelVersion"] = mlPrediction.ModelVersion;
        }

        if (!mlPrediction.DriftDetected)
        {
            signal.Metadata.Remove("FeatureDriftDetected");
            signal.Metadata.Remove("FeatureDriftCritical");
            signal.Metadata.Remove("DriftedFeatureCount");
            signal.Metadata.Remove("FeatureDriftMaxZScore");
            signal.Metadata.Remove("FeatureDriftImpactLabel");
            signal.Metadata.Remove("FeatureDriftSummary");
            return;
        }

        var impactLabel = mlPrediction.DriftIsCritical
            ? "ML drift vetoed setup"
            : "ML drift trimmed size";
        var summary = mlPrediction.DriftIsCritical
            ? $"Critical ML feature drift invalidated the setup ({mlPrediction.DriftedFeatureCount} features, max z-score {mlPrediction.MaxFeatureZScore:F2})."
            : $"Elevated ML feature drift reduced model influence and trimmed size ({mlPrediction.DriftedFeatureCount} features, max z-score {mlPrediction.MaxFeatureZScore:F2}).";

        signal.Metadata["FeatureDriftDetected"] = "true";
        signal.Metadata["FeatureDriftCritical"] = mlPrediction.DriftIsCritical ? "true" : "false";
        signal.Metadata["DriftedFeatureCount"] = mlPrediction.DriftedFeatureCount.ToString();
        signal.Metadata["FeatureDriftMaxZScore"] = mlPrediction.MaxFeatureZScore.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        signal.Metadata["FeatureDriftImpactLabel"] = impactLabel;
        signal.Metadata["FeatureDriftSummary"] = summary;
    }
}
