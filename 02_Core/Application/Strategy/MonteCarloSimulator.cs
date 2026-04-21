namespace ThatsMyQuant.Application.Strategy;

public class MonteCarloSimulator
{
    public async Task<MonteCarloResult> RunSimulationsAsync(
        List<double> historicalPnlTrades, 
        decimal startingCapital, 
        int iterations = 10000, 
        int stepsPerIteration = 252)
    {
        if (historicalPnlTrades == null || historicalPnlTrades.Count == 0)
            throw new ArgumentException("Requires at least one valid trade to simulate permutations.");

        var finalEquities = new decimal[iterations];
        var maxDrawdowns = new decimal[iterations];

        await Task.Run(() =>
        {
            Parallel.For(0, iterations, i =>
            {
                var (simEquity, maxDD) = RunSingleIteration(historicalPnlTrades, startingCapital, stepsPerIteration);
                finalEquities[i] = simEquity;
                maxDrawdowns[i] = maxDD;
            });
        });

        return FinalizeResults(finalEquities, maxDrawdowns, iterations);
    }

    private static IReadOnlyList<MonteCarloHistogramBin> BuildFinalEquityHistogram(decimal[] finalEquitiesSnapshot, int iterations)
    {
        if (finalEquitiesSnapshot.Length == 0)
        {
            return Array.Empty<MonteCarloHistogramBin>();
        }

        var binCount = Math.Clamp(iterations / 200, 8, 32);
        var min = finalEquitiesSnapshot[0];
        var max = finalEquitiesSnapshot[0];
        foreach (var v in finalEquitiesSnapshot)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (min == max)
        {
            return new[] { new MonteCarloHistogramBin(min, max, finalEquitiesSnapshot.Length) };
        }

        var width = (max - min) / binCount;
        if (width == 0m)
        {
            return new[] { new MonteCarloHistogramBin(min, max, finalEquitiesSnapshot.Length) };
        }

        var counts = new int[binCount];
        foreach (var v in finalEquitiesSnapshot)
        {
            var idx = (int)((v - min) / width);
            if (idx < 0) idx = 0;
            if (idx >= binCount) idx = binCount - 1;
            counts[idx]++;
        }

        var bins = new MonteCarloHistogramBin[binCount];
        for (var i = 0; i < binCount; i++)
        {
            var start = min + i * width;
            var end = i == binCount - 1 ? max : min + (i + 1) * width;
            bins[i] = new MonteCarloHistogramBin(start, end, counts[i]);
        }

        return bins;
    }

    private static (decimal finalEquity, decimal maxDrawdown) RunSingleIteration(List<double> trades, decimal capital, int steps)
    {
        decimal simEquity = capital;
        decimal peak = capital;
        decimal maxDD = 0;

        for (int s = 0; s < steps; s++)
        {
            decimal randomPnl = (decimal)trades[Random.Shared.Next(trades.Count)];
            simEquity += randomPnl;

            if (simEquity > peak) peak = simEquity;
            
            decimal currentDD = peak - simEquity;
            if (currentDD > maxDD) maxDD = currentDD;

            if (simEquity <= 0) { simEquity = 0; break; }
        }
        return (simEquity, maxDD);
    }

    private static MonteCarloResult FinalizeResults(decimal[] finalEquities, decimal[] maxDrawdowns, int iterations)
    {
        var histogramSource = (decimal[])finalEquities.Clone();
        var histogram = BuildFinalEquityHistogram(histogramSource, iterations);

        Array.Sort(finalEquities);
        Array.Sort(maxDrawdowns);

        var medianIdx = Math.Clamp(iterations / 2, 0, iterations - 1);
        var p5Idx = Math.Clamp((int)(iterations * 0.05d), 0, iterations - 1);
        var p99DdIdx = Math.Clamp((int)(iterations * 0.99d), 0, iterations - 1);

        return new MonteCarloResult
        {
            MedianFinalEquity = finalEquities[medianIdx],
            FifthPercentileEquity = finalEquities[p5Idx],
            NinetyNinthPercentileDrawdown = maxDrawdowns[p99DdIdx],
            FinalEquityHistogram = histogram
        };
    }
}

public readonly record struct MonteCarloHistogramBin(decimal BinStart, decimal BinEnd, int Count);

public record MonteCarloResult
{
    public decimal MedianFinalEquity { get; init; }
    public decimal FifthPercentileEquity { get; init; }
    public decimal NinetyNinthPercentileDrawdown { get; init; }
    public IReadOnlyList<MonteCarloHistogramBin> FinalEquityHistogram { get; init; } = Array.Empty<MonteCarloHistogramBin>();
}
