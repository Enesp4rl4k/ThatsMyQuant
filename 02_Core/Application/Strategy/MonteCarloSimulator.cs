using ThatsMyQuant.Domain.Enums;
using System.Collections.Concurrent;

namespace ThatsMyQuant.Application.Strategy;

public class MonteCarloSimulator
{
    public async Task<MonteCarloResult> RunSimulationsAsync(
        List<double> historicalPnlTrades, 
        decimal startingCapital, 
        int iterations = 10000, 
        int stepsPerIteration = 252)
    {
        // Guard Clause
        if (historicalPnlTrades == null || historicalPnlTrades.Count == 0)
        {
            throw new ArgumentException("Requires at least one valid trade to simulate permutations.");
        }

        // SonarQube Fix: Thread-safe koleksiyonlar veya yerel toplama (aggregation)
        // results ve drawdowns listelerini önceden boyutlandırıyoruz (Pre-allocation)
        var finalEquities = new decimal[iterations];
        var maxDrawdowns = new decimal[iterations];

        await Task.Run(() =>
        {
            // SonarQube Fix: lock yerine dizi indeksleme kullanmak lock contention'ı sıfırlar.
            Parallel.For(0, iterations, i =>
            {
                decimal simEquity = startingCapital;
                decimal peakSimEquity = startingCapital;
                decimal maxDD = 0;

                // Random.Shared thread-safe'dir, güvenle kullanılabilir.
                for (int s = 0; s < stepsPerIteration; s++)
                {
                    int randomIndex = Random.Shared.Next(historicalPnlTrades.Count);
                    decimal randomPnl = (decimal)historicalPnlTrades[randomIndex];
                    
                    simEquity += randomPnl;

                    if (simEquity > peakSimEquity) 
                        peakSimEquity = simEquity;
                    
                    decimal currentDD = peakSimEquity - simEquity;
                    if (currentDD > maxDD) 
                        maxDD = currentDD;

                    if (simEquity <= 0) 
                    {
                        simEquity = 0; // İflas (Ruin)
                        break;
                    }
                }

                // Her thread kendi dizinindeki elemana yazar, kilitlenmeye (lock) gerek kalmaz!
                finalEquities[i] = simEquity;
                maxDrawdowns[i] = maxDD;
            });
        });

        // Array.Sort, List.Sort'tan daha hızlıdır.
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