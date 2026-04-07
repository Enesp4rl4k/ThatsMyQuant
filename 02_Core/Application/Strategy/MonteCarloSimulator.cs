namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// BLUE PATH: Monte Carlo equity simulator.
/// Uses ThreadLocal&lt;Random&gt; for lock-free parallel throughput
/// instead of serializing on Random.Shared.
/// </summary>
public class MonteCarloSimulator
{
    // ThreadLocal avoids lock contention in Parallel.For — each thread gets its own RNG
    private static readonly ThreadLocal<Random> ThreadRng = new(() =>
        new Random(Interlocked.Increment(ref _seedCounter) ^ Environment.CurrentManagedThreadId));
    private static int _seedCounter = Environment.TickCount;

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

    private static (decimal finalEquity, decimal maxDrawdown) RunSingleIteration(List<double> trades, decimal capital, int steps)
    {
        var rng = ThreadRng.Value!;
        decimal simEquity = capital;
        decimal peak = capital;
        decimal maxDD = 0;

        for (int s = 0; s < steps; s++)
        {
            decimal randomPnl = (decimal)trades[rng.Next(trades.Count)];
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
        Array.Sort(finalEquities);
        Array.Sort(maxDrawdowns);

        return new MonteCarloResult
        {
            MedianFinalEquity = finalEquities[iterations / 2],
            FifthPercentileEquity = finalEquities[(int)(iterations * 0.05)],
            NinetyNinthPercentileDrawdown = maxDrawdowns[(int)(iterations * 0.99)]
        };
    }
}

public record MonteCarloResult
{
    public decimal MedianFinalEquity { get; init; }
    public decimal FifthPercentileEquity { get; init; }
    public decimal NinetyNinthPercentileDrawdown { get; init; }
}
