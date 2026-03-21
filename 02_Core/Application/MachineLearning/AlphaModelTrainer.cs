using Microsoft.Extensions.Logging;
using Microsoft.ML;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Harnesses Microsoft.ML to train a highly effective Gradient Boosted Decision Tree (FastTree) regression model.
/// It fits the historical FeatureSets mapped by the FeatureSetBuilder against the known N+1 target predictions.
/// </summary>
public class AlphaModelTrainer
{
    private readonly ILogger<AlphaModelTrainer> _logger;
    private readonly MLContext _mlContext;

    public AlphaModelTrainer(ILogger<AlphaModelTrainer> logger)
    {
        _logger = logger;
        _mlContext = new MLContext(seed: 42); // Deterministic seed for reproducible testing
    }

    /// <summary>
    /// Trains the ML Model and saves the serialized binary pipeline to the local machine (.zip).
    /// Returns the absolute mathematical R-Squared accuracy of the model against the validation set.
    /// </summary>
    public double TrainAndSaveModel(List<CandleMlData> trainingData, string modelPath = "PredictiveAlphaModel.zip")
    {
        if (trainingData.Count < 100)
        {
            _logger.LogWarning("Insufficient candle data to train ML.NET Alpha Model.");
            return 0.0;
        }

        // Load data into the high-performance ML.NET IDataView memory structure
        IDataView dataView = _mlContext.Data.LoadFromEnumerable(trainingData);

        // Split data 80/20 for Training and internal blind Accuracy Validation
        var testTrainSplit = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

        // Define the Gradient Boosted Tree algorithm
        var pipeline = _mlContext.Regression.Trainers.FastTree(
            labelColumnName: "Label",
            featureColumnName: "Features",
            numberOfLeaves: 20,
            numberOfTrees: 100,
            learningRate: 0.2);

        _logger.LogInformation("Beginning ML.NET FastTree Training Phase...");
        var model = pipeline.Fit(testTrainSplit.TrainSet);

        // Evaluate model precision against unseen data
        var predictions = model.Transform(testTrainSplit.TestSet);
        var metrics = _mlContext.Regression.Evaluate(predictions, labelColumnName: "Label");

        _logger.LogInformation($"Model Evaluated. R-Squared: {metrics.RSquared:F4} | Root Mean Squared Error: {metrics.RootMeanSquaredError:F4}");

        // Save binary weights to disk for ultra-fast Inference Loading later
        _mlContext.Model.Save(model, dataView.Schema, modelPath);
        _logger.LogInformation($"Alpha Model saved successfully to {modelPath}");

        return metrics.RSquared;
    }
}
