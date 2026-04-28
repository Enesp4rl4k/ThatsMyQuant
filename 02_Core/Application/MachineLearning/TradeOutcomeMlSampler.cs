using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Contracts.Notifications;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Bridges the trade lifecycle to the ML training pipeline. On every
/// <see cref="PositionClosedNotification"/> we capture a labelled feature sample
/// — but ONLY for directional strategies whose tag matches
/// <see cref="DirectionalStrategyTags"/>.
///
/// Pairs / funding / arbitrage trades emit closures too, but the live
/// <see cref="FeatureEngineeringAgent"/> tensor is an RSI/EMA/MA/OFI/tick-direction
/// snapshot — it has nothing to do with z-score state, funding rate momentum,
/// or basis spread. Feeding pair/funding outcomes labelled by directional
/// features produces noise (memory: followup_ml_strategy_specific_2026_04_25).
///
/// Strategy-specific feature pipelines (PairFeatureEngineeringAgent,
/// FundingFeatureEngineeringAgent) are deferred — the placeholder filter is
/// the safest default until those land.
/// </summary>
public sealed class TradeOutcomeMlSampler : INotificationHandler<PositionClosedNotification>
{
    public static readonly HashSet<string> DirectionalStrategyTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "TrendConfluence",
        "TrendRunner",
        "MeanReversion",
        "Breakout",
        "Directional",
    };

    private readonly ModelTrainingPipeline _pipeline;
    private readonly ILogger<TradeOutcomeMlSampler> _logger;

    public TradeOutcomeMlSampler(
        ModelTrainingPipeline pipeline,
        ILogger<TradeOutcomeMlSampler> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public Task Handle(PositionClosedNotification n, CancellationToken ct)
    {
        var tag = n.StrategyTag ?? "";
        if (!DirectionalStrategyTags.Contains(tag))
        {
            _logger.LogTrace(
                "TradeOutcomeMlSampler: skipping {Symbol} closure — strategy tag {Tag} is not directional.",
                n.Symbol, tag);
            return Task.CompletedTask;
        }

        try
        {
            // RecordSample reads the current FeatureEngineeringAgent tensor and pairs
            // it with the trade outcome label. Build a minimal Trade just to carry
            // Symbol — the pipeline doesn't read other Trade fields.
            var stub = new Trade
            {
                Symbol = n.Symbol,
                Side = n.Side,
                EntryPrice = n.EntryPrice,
                ExitPrice = n.ExitPrice,
                Quantity = n.Quantity,
                StrategyTag = tag,
                EntryTime = n.EntryTimeUtc,
                ExitTime = n.ExitTimeUtc,
            };

            _pipeline.RecordSample(stub, n.NetPnl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TradeOutcomeMlSampler: RecordSample failed for {Symbol}.", n.Symbol);
        }

        return Task.CompletedTask;
    }
}
