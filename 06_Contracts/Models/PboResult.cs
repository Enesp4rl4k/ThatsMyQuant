namespace ThatsMyQuant.Contracts.Models;

public record PboResult
{
    public double PBO { get; init; }
    public double StochasticDominance { get; init; }
    public double ExpectedPerformance { get; init; }
    public double PerformanceDecay { get; init; }
    public int TotalCombinations { get; init; }
    public int OverfittingCount { get; init; }
    public List<double> TrainSharpeRatios { get; init; } = new();
    public List<double> TestSharpeRatios { get; init; } = new();
    public List<double> LogitWeights { get; init; } = new();
}
