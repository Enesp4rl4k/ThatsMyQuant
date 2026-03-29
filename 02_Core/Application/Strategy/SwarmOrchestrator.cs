using System.Threading;
using System.Threading.Tasks;
using MediatR;
using ThatsMyQuant.Application.MachineLearning;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ThatsMyQuant.Application.Common;

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
    private readonly SwarmOptions _opts;

    public SwarmOrchestrator(
        RiskAgent riskAgent,
        SentimentAgent sentimentAgent,
        InferenceAgent inferenceAgent,
        FeatureEngineeringAgent featureAgent,
        IPortfolioStateCache portfolioCache,
        OFIExecutionAgent ofiAgent,
        IAgentCoordinator agentCoordinator,
        ILogger<SwarmOrchestrator> logger,
        IOptions<SwarmOptions>? options = null)
    {
        _riskAgent = riskAgent;
        _sentimentAgent = sentimentAgent;
        _inferenceAgent = inferenceAgent;
        _featureAgent = featureAgent;
        _portfolioCache = portfolioCache;
        _ofiAgent = ofiAgent;
        _agentCoordinator = agentCoordinator;
        _logger = logger;
        _opts = options?.Value ?? new SwarmOptions();
    }

    public async Task<SwarmConsensusResult> Handle(SwarmNegotiationRequest request, CancellationToken cancellationToken)
    {
        TradingMetrics.SwarmNegotiations.Add(1);
        var sw = Stopwatch.StartNew();
        var result = new SwarmConsensusResult { Approved = false };

        // Snapshot portfolio once — avoids 3 list rebuilds per signal
        var recentTrades = _portfolioCache.GetRecentTrades();

        // --- Step 1: Portfolio guardrails (risk-critical — veto on failure) ---
        try
        {
            var regime = MarketRegimeDetector.Detect(request.Candles);
            var riskParams = ResolveRegimeAdjustedRisk(request.Risk, regime);

            var portfolioGuard = await _riskAgent.EvaluatePortfolioGuardrailsAsync(
                recentTrades,
                request.Signal,
                request.InitialDecision.PositionSizePercent,
                riskParams);

            if (!portfolioGuard.Approved)
            {
                _logger.LogInformation("[PortfolioGuard] Vetoed {Symbol}: {Reason}", request.Signal.Symbol, portfolioGuard.Reason);
                result.RejectionFlags |= SignalReasonFlags.RiskVeto;
                return result;
            }

            // --- Step 2: Concurrent agents (each handled independently) ---
            var regime2 = regime; // capture for closure
            var riskParams2 = riskParams;

            var (sentimentVote, riskVote) = await RunConcurrentAgentsSafe(request, riskParams2, result, cancellationToken);

            // --- Step 3: ML inference (already safe — returns IsValid=false on any issue) ---
            var mlPrediction = _inferenceAgent.PredictNextReturn(_featureAgent.GetSnapshot());

            // --- Step 4: Strategy votes (non-risk — abstain on failure) ---
            IReadOnlyList<StrategyVote> strategyVotes = Array.Empty<StrategyVote>();
            try
            {
                using var voteCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                voteCts.CancelAfter(_opts.AgentTimeoutMs);
                strategyVotes = await _agentCoordinator.CollectStrategyVotesAsync(
                    request.Signal, request.Patterns, request.Foundation,
                    request.Candles, regime2.Regime, voteCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TradingMetrics.AgentTimeouts.Add(1, new KeyValuePair<string, object?>("step", "strategy_votes"));
                _logger.LogWarning("[AgentCoordinator.StrategyVotes] Timed out for {Symbol} after {Ms}ms — abstaining", request.Signal.Symbol, _opts.AgentTimeoutMs);
                result.RejectionFlags |= SignalReasonFlags.SystemError;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[AgentCoordinator.StrategyVotes] Faulted for {Symbol} — abstaining from strategy vote", request.Signal.Symbol);
                result.RejectionFlags |= SignalReasonFlags.SystemError;
            }

            // --- Step 5: Meta-adaptation (non-risk — use defaults on failure) ---
            var metaAdaptation = MetaAdaptation.Default;
            try
            {
                using var metaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                metaCts.CancelAfter(_opts.AgentTimeoutMs);
                metaAdaptation = await _agentCoordinator.RunMetaAdaptationAsync(
                    recentTrades, regime2.Regime, metaCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TradingMetrics.AgentTimeouts.Add(1, new KeyValuePair<string, object?>("step", "meta_adaptation"));
                _logger.LogWarning("[AgentCoordinator.MetaAdaptation] Timed out for {Symbol} after {Ms}ms — using defaults", request.Signal.Symbol, _opts.AgentTimeoutMs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[AgentCoordinator.MetaAdaptation] Faulted for {Symbol} — using default weights", request.Signal.Symbol);
            }

            float confidenceScore = CalculateConfidence(
                sentimentVote, riskVote, mlPrediction, request.InitialDecision,
                strategyVotes, metaAdaptation, result);

            // --- Step 6: Microstructure / OFI injection (non-risk — skip on failure) ---
            try
            {
                confidenceScore = await ApplyMicrostructureInjection(request.Signal, result, confidenceScore, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[OFIExecutionAgent] Faulted for {Symbol} — microstructure injection skipped", request.Signal.Symbol);
            }

            // --- Step 7: Risk gauntlet (risk-critical — veto on failure/timeout) ---
            try
            {
                using var riskCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                riskCts.CancelAfter(_opts.AgentTimeoutMs);
                var riskGauntletResult = await _agentCoordinator.RunRiskGauntletAsync(
                    request.Signal, request.InitialDecision, recentTrades, riskCts.Token);

                if (!riskGauntletResult.Approved)
                {
                    result.RejectionFlags |= riskGauntletResult.VetoFlag;
                    _logger.LogWarning("[RiskGauntlet] Vetoed {Symbol}: {Reason}", request.Signal.Symbol, riskGauntletResult.Reason);
                    return result;
                }

                FinalizeConsensus(confidenceScore, riskVote, mlPrediction, request.InitialDecision,
                    riskGauntletResult.SizeMultiplier, result);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TradingMetrics.AgentTimeouts.Add(1, new KeyValuePair<string, object?>("step", "risk_gauntlet"));
                _logger.LogWarning("[AgentCoordinator.RiskGauntlet] Timed out for {Symbol} after {Ms}ms — conservative veto", request.Signal.Symbol, _opts.AgentTimeoutMs);
                result.RejectionFlags |= SignalReasonFlags.RiskVeto | SignalReasonFlags.SystemError;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[AgentCoordinator.RiskGauntlet] Faulted for {Symbol} — conservative veto applied", request.Signal.Symbol);
                result.RejectionFlags |= SignalReasonFlags.RiskVeto | SignalReasonFlags.SystemError;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[SwarmOrchestrator] Cancelled for {Symbol}", request.Signal.Symbol);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "[SwarmOrchestrator] Unexpected orchestration fault for {Symbol} — all agents aborted", request.Signal.Symbol);
            result.Approved = false;
            result.RejectionFlags |= SignalReasonFlags.SystemError;
        }

        sw.Stop();
        TradingMetrics.SwarmLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("symbol", request.Signal.Symbol));

        if (result.Approved)
            TradingMetrics.SwarmApprovals.Add(1);
        else
            TradingMetrics.SwarmVetos.Add(1);

        return result;
    }

    private RiskParameters ResolveRegimeAdjustedRisk(RiskParameters current, MarketRegimeDetector.RegimeResult regime)
    {
        if (current != RiskParameters.Default) return current;
        return regime.Regime switch
        {
            MarketRegime.Volatile => RiskParameters.Default with { MaxPositionSizePercent = _opts.VolatileRegimeMaxPositionPct },
            MarketRegime.Trending => RiskParameters.Default with { MaxPositionSizePercent = _opts.TrendingRegimeMaxPositionPct },
            _ => RiskParameters.Default
        };
    }

    /// <summary>
    /// Runs sentiment and risk agents concurrently with independent fault isolation.
    /// Sentiment failure → abstain (non-critical). Risk agent failure → conservative veto (safety-first).
    /// </summary>
    private async Task<((bool Approved, string Reason) Sentiment, (bool Approved, string Reason, decimal SafePositionSizePercent) Risk)>
        RunConcurrentAgentsSafe(SwarmNegotiationRequest request, RiskParameters riskParams, SwarmConsensusResult result, CancellationToken ct)
    {
        var sentimentTask = RunSentimentAgentSafe(request.Signal.Symbol, ct);
        var riskTask = RunRiskAgentSafe(request.InitialDecision, riskParams, request.Signal.Volume24h);

        await Task.WhenAll(sentimentTask, riskTask).WaitAsync(ct);
        return (await sentimentTask, await riskTask);
    }

    private async Task<(bool Approved, string Reason)> RunSentimentAgentSafe(string symbol, CancellationToken ct)
    {
        try
        {
            return await _sentimentAgent.EvaluateAsync(symbol, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SentimentAgent] Faulted for {Symbol} — abstaining from vote", symbol);
            return (false, $"SentimentAgent faulted: {ex.GetType().Name}");
        }
    }

    private async Task<(bool Approved, string Reason, decimal SafePositionSizePercent)> RunRiskAgentSafe(
        TradeDecision decision, RiskParameters riskParams, decimal volume24h)
    {
        try
        {
            return await _riskAgent.EvaluateAsync(decision, riskParams, volume24h);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RiskAgent.Evaluate] Faulted — conservative veto applied");
            return (false, $"RiskAgent faulted: {ex.GetType().Name}", 0m);
        }
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
        if (sentiment.Approved) score += _opts.SentimentWeight; else result.RejectionFlags |= SignalReasonFlags.SentimentVeto;
        if (risk.Approved) score += _opts.RiskWeight; else result.RejectionFlags |= SignalReasonFlags.RiskVeto;

        if (ml.IsValid)
        {
            // ML predicting negative return = hard veto (no negative expected value trades)
            if (ml.Value < 0 && decision.Side == TradeSide.Buy)
            {
                result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
                return 0f; // ML veto — don't trade against the model
            }
            else if (ml.Value > 0 && decision.Side == TradeSide.Sell)
            {
                result.RejectionFlags |= SignalReasonFlags.MachineLearningVeto;
                return 0f;
            }
            else score += _opts.MlWeight;
        }

        if (strategyVotes.Count > 0)
        {
            var voteScore = ComputeWeightedVoteScore(strategyVotes, meta);
            score += voteScore * _opts.StrategyVoteWeight;
            _logger.LogDebug("Strategy votes contribution: {Score:F3} ({Count} votes)", voteScore, strategyVotes.Count);
        }

        score *= meta.ConfidenceScalar;

        return score;
    }

    /// <summary>
    /// Computes a weighted consensus score from strategy votes.
    /// Weight source priority: RL bandit (data-driven) → agent's PriorWeight (design-time).
    /// PriorWeight is the agent's SwarmWeight carried on the vote, normalized so the average
    /// prior equals 1.0. Once the RL bandit has enough data, its learned weights override.
    /// </summary>
    private float ComputeWeightedVoteScore(IReadOnlyList<StrategyVote> votes, MetaAdaptation meta)
    {
        float weightedSum = 0f, totalWeight = 0f;

        // Normalize prior weights so their average = 1.0
        var activeVotes = votes.Where(v => v.Vote != VoteOutcome.Abstain).ToList();
        // Require >50% agent participation — if most agents abstain, signal is too weak
        if (activeVotes.Count == 0 || activeVotes.Count < votes.Count * _opts.MinParticipationRate) return 0f;

        float avgPrior = activeVotes.Average(v => (float)v.PriorWeight);
        if (avgPrior <= 0) avgPrior = 1f;

        foreach (var vote in activeVotes)
        {
            // RL bandit weight takes priority; fall back to normalized prior weight
            float baseWeight;
            if (meta.StrategyWeights.TryGetValue(vote.StrategyTag, out var rlWeight))
                baseWeight = rlWeight;
            else
                baseWeight = vote.PriorWeight / avgPrior;

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
            if (result.ExecutionAction != ExecutionActionType.MarketTaker) return confidence + _opts.OfiConfidenceBoost;
        }
        return confidence;
    }

    private void FinalizeConsensus(float score, (bool Approved, string Reason, decimal Size) risk, PredictionResult ml,
        TradeDecision decision, decimal sizeMultiplier, SwarmConsensusResult result)
    {
        if (score >= _opts.SuccessThreshold && result.RejectionFlags == SignalReasonFlags.None)
        {
            result.Approved = true;
            var mlAdjusted = Math.Clamp((decimal)(1.0f + ml.Value), _opts.MlSizeClampMin, _opts.MlSizeClampMax);
            result.AdjustedKellyFraction = risk.Size * mlAdjusted * sizeMultiplier;
            result.AdjustedTakeProfit = decision.TakeProfit;
            if (result.ExecutionAction == ExecutionActionType.None) result.ExecutionAction = ExecutionActionType.MarketTaker;
        }
    }
}
