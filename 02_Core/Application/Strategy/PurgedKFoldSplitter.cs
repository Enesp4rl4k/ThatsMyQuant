using System;
using System.Collections.Generic;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Purged K-Fold cross-validation for time-series ML — Lopez de Prado (2018),
/// "Advances in Financial Machine Learning", Ch. 7. Standard K-fold leaks
/// information across the train/test boundary because adjacent samples in
/// time are highly correlated; trade outcome at bar t is informed by features
/// computed from bars t-K to t-1 which may overlap the training window.
///
/// This splitter applies two corrections:
///   • <b>Purge</b>: training samples whose feature window overlaps the test
///     fold's labels are dropped from training.
///   • <b>Embargo</b>: training samples immediately AFTER the test fold's
///     labels are dropped (forward-looking leakage).
///
/// Used in upgrade plan: any ML composite (Phase F XGBoost) MUST use this
/// splitter — naïve K-fold inflates OOS metrics by 30-50% in financial
/// time series. Also used to compute backtest-overfit-resistant Sharpe / IC
/// estimates for any strategy that is ranked across many parameter sets.
/// </summary>
public static class PurgedKFoldSplitter
{
    /// <summary>
    /// One train/test split: index sets relative to the original 0..N-1 sample array.
    /// </summary>
    public sealed record Split(int FoldIndex, IReadOnlyList<int> TrainIndices, IReadOnlyList<int> TestIndices);

    /// <summary>
    /// Generate K purged-and-embargoed train/test splits.
    /// </summary>
    /// <param name="numSamples">Total samples in chronological order (must be ≥ 2·numFolds).</param>
    /// <param name="numFolds">K — number of contiguous test folds. Default 5.</param>
    /// <param name="purgeBars">Number of bars dropped from training at each test-fold boundary.
    /// Should match the maximum lookback of any feature used (e.g., 60 for OFI features computed
    /// over a 60-bar window). Default 0 (no purge — equivalent to standard K-fold).</param>
    /// <param name="embargoBars">Number of bars dropped from training AFTER each test fold.
    /// Should match the maximum forward horizon of labels (e.g., 12 if labels look 12 bars ahead).
    /// Default 0.</param>
    public static IReadOnlyList<Split> Generate(
        int numSamples,
        int numFolds = 5,
        int purgeBars = 0,
        int embargoBars = 0)
    {
        if (numSamples < 2 * numFolds)
            throw new ArgumentException($"Need at least {2 * numFolds} samples for {numFolds} folds.", nameof(numSamples));
        if (numFolds < 2)
            throw new ArgumentOutOfRangeException(nameof(numFolds));
        if (purgeBars < 0 || embargoBars < 0)
            throw new ArgumentOutOfRangeException(nameof(purgeBars));

        var splits = new List<Split>(numFolds);
        // Contiguous fold boundaries.
        int foldSize = numSamples / numFolds;

        for (int k = 0; k < numFolds; k++)
        {
            int testStart = k * foldSize;
            int testEnd = (k == numFolds - 1) ? numSamples : (k + 1) * foldSize;

            // Test indices.
            var testList = new List<int>(testEnd - testStart);
            for (int i = testStart; i < testEnd; i++) testList.Add(i);

            // Train indices: everything outside [testStart - purge, testEnd + embargo].
            int purgeLowerBound = testStart - purgeBars;
            int embargoUpperBound = testEnd + embargoBars;
            var trainList = new List<int>(numSamples - testList.Count);
            for (int i = 0; i < numSamples; i++)
            {
                if (i >= purgeLowerBound && i < embargoUpperBound) continue;
                trainList.Add(i);
            }

            splits.Add(new Split(k, trainList, testList));
        }
        return splits;
    }

    /// <summary>
    /// Estimate the Probability of Backtest Overfitting (PBO) per Bailey-Lopez de Prado
    /// from a matrix of in-sample / out-of-sample Sharpe pairs across N candidate
    /// parameter sets. PBO is the probability that the IS-best parameter set
    /// underperforms the OOS median — a proxy for "is our backtest leaderboard noise?".
    /// Lower is better; PBO &lt; 0.5 means the leaderboard contains real signal.
    /// </summary>
    /// <param name="inSampleSharpes">Sharpe per candidate (length N).</param>
    /// <param name="outOfSampleSharpes">Same N candidates' OOS Sharpes.</param>
    public static double ComputePbo(IReadOnlyList<double> inSampleSharpes, IReadOnlyList<double> outOfSampleSharpes)
    {
        ArgumentNullException.ThrowIfNull(inSampleSharpes);
        ArgumentNullException.ThrowIfNull(outOfSampleSharpes);
        if (inSampleSharpes.Count != outOfSampleSharpes.Count)
            throw new ArgumentException("IS and OOS arrays must align.");

        int n = inSampleSharpes.Count;
        if (n < 2) return 1.0;

        // Find IS-winner index.
        int winner = 0;
        for (int i = 1; i < n; i++)
            if (inSampleSharpes[i] > inSampleSharpes[winner]) winner = i;

        // OOS rank of winner (1-indexed). PBO = #{candidates with higher OOS than winner} / (n-1).
        int oosBetterCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (i == winner) continue;
            if (outOfSampleSharpes[i] > outOfSampleSharpes[winner]) oosBetterCount++;
        }
        return (double)oosBetterCount / (n - 1);
    }
}
