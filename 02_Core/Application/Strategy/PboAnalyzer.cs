namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Probability of Backtest Overfitting (PBO) analyzer.
/// Implements Combinatorial Symmetric Cross-Validation (CSCV) as described by
/// Bailey, Borwein, Lopez de Prado, and Zhu (2014).
///
/// Tests whether a strategy's backtest performance is genuine alpha or curve-fitting luck
/// by partitioning historical data into subsets and checking if the best in-sample (IS)
/// configuration also performs well out-of-sample (OOS).
/// </summary>
public static class PboAnalyzer
{
    public sealed record PboResult
    {
        /// <summary>Probability of Backtest Overfitting (0 to 1). Above 0.50 = likely overfitted.</summary>
        public double Pbo { get; init; }

        /// <summary>Whether the strategy is likely overfitted.</summary>
        public bool IsOverfitted => Pbo > 0.50;

        /// <summary>Number of combinatorial trials performed.</summary>
        public int TrialCount { get; init; }

        /// <summary>Fraction of trials where OOS rank of IS-best config was below median.</summary>
        public double OosRankDegradation { get; init; }

        /// <summary>Average OOS Sharpe of the IS-best configurations across all trials.</summary>
        public double AverageOosSharpe { get; init; }

        /// <summary>Minimum OOS Sharpe of the IS-best configurations (worst-case).</summary>
        public double MinOosSharpe { get; init; }

        /// <summary>Descriptive verdict.</summary>
        public string Verdict { get; init; } = "";
    }

    /// <summary>
    /// Runs PBO analysis via Combinatorial Symmetric Cross-Validation.
    ///
    /// <paramref name="performanceMatrix"/> is a matrix where rows = time partitions (S),
    /// columns = strategy configurations (N). Each cell is the Sharpe (or return) of
    /// config j during partition i.
    ///
    /// The matrix is split into S/2 in-sample and S/2 out-of-sample blocks across
    /// all C(S, S/2) combinations (capped at <paramref name="maxTrials"/>).
    /// </summary>
    /// <param name="performanceMatrix">S rows x N columns of per-partition performance.</param>
    /// <param name="maxTrials">Cap on combinatorial trials to keep runtime bounded.</param>
    public static PboResult Analyze(double[,] performanceMatrix, int maxTrials = 2000)
    {
        int partitions = performanceMatrix.GetLength(0);
        int configs = performanceMatrix.GetLength(1);

        if (partitions < 4 || configs < 2)
            return new PboResult { Verdict = "Insufficient data for PBO analysis." };

        int halfS = partitions / 2;
        var allCombinations = GenerateCombinations(partitions, halfS, maxTrials);
        int overfitCount = 0;
        double totalOosSharpe = 0;
        double minOosSharpe = double.MaxValue;
        int totalRankDegraded = 0;

        foreach (var isIndices in allCombinations)
        {
            var oosIndices = Enumerable.Range(0, partitions).Except(isIndices).ToArray();

            // Compute IS performance for each config
            var isPerformance = new double[configs];
            var oosPerformance = new double[configs];

            for (int j = 0; j < configs; j++)
            {
                isPerformance[j] = isIndices.Sum(i => performanceMatrix[i, j]) / isIndices.Length;
                oosPerformance[j] = oosIndices.Sum(i => performanceMatrix[i, j]) / oosIndices.Length;
            }

            // Find best IS config
            int bestIsConfig = 0;
            for (int j = 1; j < configs; j++)
            {
                if (isPerformance[j] > isPerformance[bestIsConfig])
                    bestIsConfig = j;
            }

            double bestIsOos = oosPerformance[bestIsConfig];
            totalOosSharpe += bestIsOos;
            if (bestIsOos < minOosSharpe) minOosSharpe = bestIsOos;

            // Compute OOS rank of the IS-best config (1 = best, N = worst)
            int oosRank = oosPerformance.Count(p => p >= bestIsOos);

            // If rank is below median, it's a sign of overfitting
            if (oosRank > configs / 2)
                totalRankDegraded++;

            // If IS-best config has negative or below-median OOS performance
            double medianOos = Median(oosPerformance);
            if (bestIsOos <= medianOos)
                overfitCount++;
        }

        int trials = allCombinations.Count;
        double pbo = trials > 0 ? (double)overfitCount / trials : 0;
        double avgOosSharpe = trials > 0 ? totalOosSharpe / trials : 0;
        double rankDegradation = trials > 0 ? (double)totalRankDegraded / trials : 0;

        string verdict = pbo switch
        {
            <= 0.15 => "Strong edge — low probability of overfitting.",
            <= 0.30 => "Moderate confidence — some overfitting risk. Consider reducing parameters.",
            <= 0.50 => "Warning — near coin-flip between real edge and curve-fitting.",
            <= 0.75 => "Likely overfitted — the strategy probably won't replicate live.",
            _ => "Severe overfitting — backtest results are almost certainly luck."
        };

        return new PboResult
        {
            Pbo = pbo,
            TrialCount = trials,
            OosRankDegradation = rankDegradation,
            AverageOosSharpe = avgOosSharpe,
            MinOosSharpe = minOosSharpe == double.MaxValue ? 0 : minOosSharpe,
            Verdict = verdict
        };
    }

    /// <summary>
    /// Convenience method: builds a performance matrix from per-config backtest returns
    /// split into equal time partitions, then runs PBO.
    /// </summary>
    /// <param name="configReturns">List of return series, one per strategy configuration.</param>
    /// <param name="partitionCount">How many time partitions (must be even, >=4).</param>
    public static PboResult AnalyzeFromReturns(
        IReadOnlyList<IReadOnlyList<decimal>> configReturns,
        int partitionCount = 16,
        int maxTrials = 2000)
    {
        if (configReturns.Count < 2) return new PboResult { Verdict = "Need at least 2 configs." };

        int minLen = configReturns.Min(r => r.Count);
        if (minLen < partitionCount) partitionCount = Math.Max(4, minLen);
        if (partitionCount % 2 != 0) partitionCount--;
        if (partitionCount < 4) return new PboResult { Verdict = "Insufficient data." };

        int barsPerPartition = minLen / partitionCount;
        var matrix = new double[partitionCount, configReturns.Count];

        for (int j = 0; j < configReturns.Count; j++)
        {
            for (int s = 0; s < partitionCount; s++)
            {
                int start = s * barsPerPartition;
                var slice = configReturns[j].Skip(start).Take(barsPerPartition).ToList();
                matrix[s, j] = ComputeSharpe(slice);
            }
        }

        return Analyze(matrix, maxTrials);
    }

    private static double ComputeSharpe(List<decimal> returns)
    {
        if (returns.Count < 2) return 0;
        double avg = returns.Average(r => (double)r);
        double variance = returns.Sum(r => Math.Pow((double)r - avg, 2)) / (returns.Count - 1);
        double stdDev = Math.Sqrt(variance);
        return stdDev > 0 ? avg / stdDev : 0;
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int n = sorted.Length;
        return n % 2 == 0
            ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
            : sorted[n / 2];
    }

    /// <summary>
    /// Generates random C(n, k) combinations, capped at maxTrials.
    /// For small n, generates all combinations; for large n, uses random sampling.
    /// </summary>
    private static List<int[]> GenerateCombinations(int n, int k, int maxTrials)
    {
        long totalCombinations = BinomialCoefficient(n, k);

        if (totalCombinations <= maxTrials)
            return GenerateAllCombinations(n, k);

        return GenerateRandomCombinations(n, k, maxTrials);
    }

    private static List<int[]> GenerateAllCombinations(int n, int k)
    {
        var result = new List<int[]>();
        var current = new int[k];

        void Generate(int start, int depth)
        {
            if (depth == k)
            {
                result.Add((int[])current.Clone());
                return;
            }
            for (int i = start; i <= n - k + depth; i++)
            {
                current[depth] = i;
                Generate(i + 1, depth + 1);
            }
        }

        Generate(0, 0);
        return result;
    }

    private static List<int[]> GenerateRandomCombinations(int n, int k, int maxTrials)
    {
        var rng = new Random(42); // Deterministic seed for reproducibility
        var seen = new HashSet<string>();
        var result = new List<int[]>();
        var indices = Enumerable.Range(0, n).ToArray();

        while (result.Count < maxTrials)
        {
            // Fisher-Yates partial shuffle
            var shuffled = (int[])indices.Clone();
            for (int i = 0; i < k; i++)
            {
                int j = rng.Next(i, n);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            var combo = shuffled.Take(k).OrderBy(x => x).ToArray();
            var key = string.Join(",", combo);
            if (seen.Add(key))
                result.Add(combo);
        }

        return result;
    }

    private static long BinomialCoefficient(int n, int k)
    {
        if (k > n - k) k = n - k;
        long result = 1;
        for (int i = 0; i < k; i++)
        {
            result = result * (n - i) / (i + 1);
            if (result > 1_000_000) return result; // Overflow guard
        }
        return result;
    }
}
