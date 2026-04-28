namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Shared constants for Walk-Forward Analysis (WalkForwardEngine) and
/// Walk-Forward Optimizer (WalkForwardOptimizer). Centralised here to
/// eliminate the previous duplication of the same magic numbers in both files.
/// </summary>
internal static class WalkForwardConstants
{
    /// <summary>Minimum number of candles required in the training segment of a WFO/WFA window.</summary>
    internal const int MinTrainBars = 80;

    /// <summary>Minimum number of candles required in the out-of-sample test segment of a WFO/WFA window.</summary>
    internal const int MinTestBars = 30;
}
