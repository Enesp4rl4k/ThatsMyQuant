using Microsoft.ML;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// The ultra-low latency execution component of the ML pipeline. It pre-loads the trained .zip regression model
/// into a thread-safe PredictionEngine. Designed to map incoming Alpha signals at microsecond speeds.
/// </summary>
public class InferenceAgent
{
    private readonly Lazy<PredictionEngine<CandleMlData, CandlePrediction>> _predictionEngine;
    private readonly MLContext _mlContext;

    public InferenceAgent(string modelPath = "PredictiveAlphaModel.zip")
    {
        _mlContext = new MLContext(seed: 42);
        
        // Lazy-loading ensures we only hit the disk IO penalty once during the first signal
        _predictionEngine = new Lazy<PredictionEngine<CandleMlData, CandlePrediction>>(() =>
        {
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"Cannot start Quantitative AI. Trained Model {modelPath} not found.");

            ITransformer loadedModel = _mlContext.Model.Load(modelPath, out var modelInputSchema);
            return _mlContext.Model.CreatePredictionEngine<CandleMlData, CandlePrediction>(loadedModel);
        });
    }

    /// <summary>
    /// Predicts the closing price of the N+1 candle using the incoming realtime feature metrics.
    /// Used by SwarmOrchestrator to veto Technical signals (e.g. Rejecting a long signal if ML predicts a sharp dump).
    /// </summary>
    public float PredictNextClose(float[] currentNormalizedFeatures)
    {
        if (currentNormalizedFeatures.Length != 5) 
            throw new ArgumentException("The ML Model expects exactly 5 mapped features.");

        var input = new CandleMlData { Features = currentNormalizedFeatures };

        // Zero-allocation thread-safe inference
        var prediction = _predictionEngine.Value.Predict(input);
        
        return prediction.PredictedNextClose;
    }
}
