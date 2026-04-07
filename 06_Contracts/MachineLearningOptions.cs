namespace ThatsMyQuant.Contracts.Options;

public sealed class MachineLearningOptions
{
    // === AlphaModelTrainer ===
    public int Seed { get; set; } = 42;
    public int MinTrainingData { get; set; } = 100;
    public double TestFraction { get; set; } = 0.2;
    public int NumberOfTrees { get; set; } = 100;
    public int NumberOfLeaves { get; set; } = 20;
    public double LearningRate { get; set; } = 0.2;

    // === ModelTrainingPipeline ===
    public int MinimumTrainableSamples { get; set; } = 50;
    public double L2Regularization { get; set; } = 0.0005;

    // === TickReplayEngine ===
    public decimal SimulationCommissionRate { get; set; } = 0.0005m;
    public double SimulationSlippageBps { get; set; } = 1.0;
    public int MinTicksForEntry { get; set; } = 100;
}
