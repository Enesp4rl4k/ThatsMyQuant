using ThatsMyQuant.Domain.Enums;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A high-performance probabilistic stress tester. Generates thousands of randomized permutation streams 
/// (bootstrapping with replacement) over historical trade returns to discover true Maximum Drawdowns.
/// </summary>
public class MonteCarloSimulator
{
    private readonly Random _rng = new Random();

    /// <summary>
    /// Executes a parallelized Monte Carlo permutation generator across the trade logs. 
    /// Helps quants visualize the 95th and 99th percentile worst-case risk of ruin curves.
    /// </summary>
    public async Task<MonteCarloResult> RunSimulationsAsync(
        List<double> historicalPnlTrades, 
        decimal startingCapital, 
        int iterations = 10000, 
        int stepsPerIteration = 252)
    {
        if (historicalPnlTrades == null || historicalPnlTrades.Count == 0)
        {
            throw new ArgumentException("Requires at least one valid trade to simulate permutations.");
        }

        var results = new List<decimal>();
        var drawdowns = new List<decimal>();
        
        // Use high-performance parallel execution for tens of thousands of grid bounds
        await Task.Run(() =>
        {
            Parallel.For(0, iterations, _ =>
            {
                decimal simEquity = startingCapital;
                decimal peakSimEquity = startingCapital;
                decimal maxDrawdown = 0;

                for (int s = 0; s < stepsPerIteration; s++)
                {
                    // Pick a random past trade result to shape the distribution matrix
                    var randomPnl = (decimal)historicalPnlTrades[_rng.Next(historicalPnlTrades.Count)];
                    simEquity += randomPnl;

                    if (simEquity > peakSimEquity) peakSimEquity = simEquity;
                    decimal dd = peakSimEquity - simEquity;
                    if (dd > maxDrawdown) maxDrawdown = dd;

                    if (simEquity <= 0) break; // Risk of absolute ruin triggered
                }

                lock (results)
                {
                    results.Add(simEquity);
                    drawdowns.Add(maxDrawdown);
                }
            });
        });

        results.Sort();
        drawdowns.Sort();

        return new MonteCarloResult
        {
            MedianFinalEquity = results[results.Count / 2],
            FifthPercentileEquity = results[(int)(results.Count * 0.05)], // 95% certainty boundary
            NinetyNinthPercentileDrawdown = drawdowns[(int)(drawdowns.Count * 0.99)] // 99% certainty maximum DD
        };
    }
}

public class MonteCarloResult
{
    public decimal MedianFinalEquity { get; set; }
    public decimal FifthPercentileEquity { get; set; }
    public decimal NinetyNinthPercentileDrawdown { get; set; }
}
