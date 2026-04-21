namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Distinguishes live capital from research paths so edge gates (e.g. probe band, liquidity hard gate) can align with paper/backtest fills.
/// </summary>
public enum EdgePipelineKind
{
    Live = 0,
    Paper = 1,
    Backtest = 2
}

/// <summary>
/// Options for <see cref="EdgeScoreService.Evaluate"/> — relaxes certain hard gates for simulated pipelines and allows probe-sized research trades.
/// </summary>
public sealed record EdgeScoreEvaluationOptions(
    EdgePipelineKind PipelineKind = EdgePipelineKind.Live,
    decimal SimulatedProbeSizeMultiplier = 0.45m);
