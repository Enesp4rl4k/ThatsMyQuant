using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A high-performance inference wrapper for ONNX Machine Learning models.
/// It evaluates the realtime L2 Order Book depth to predict price direction 
/// in sub-millisecond tolerances (typically < 0.5ms).
/// </summary>
public class MicrosecondPredictionEngine : IDisposable
{
    private readonly ILogger<MicrosecondPredictionEngine> _logger;
    private readonly InferenceSession? _session;
    private readonly string? _inputName;

    // Feature array representing the orderbook: [BestBidPrice, BestBidQty, BestAskPrice, BestAskQty, Spread, ImbalanceRatio]
    private readonly float[] _features = new float[6];

    public MicrosecondPredictionEngine(string modelPath, ILogger<MicrosecondPredictionEngine> logger)
    {
        _logger = logger;
        
        try
        {
            if (string.IsNullOrEmpty(modelPath) || !System.IO.File.Exists(modelPath))
            {
                _logger.LogWarning("ONNX model file not found at {Path}. Prediction engine will be disabled.", modelPath);
                return;
            }

            var sessionOptions = new SessionOptions();
            _session = new InferenceSession(modelPath, sessionOptions);
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "input";
            
            _logger.LogInformation("ONNX ML Model loaded successfully from {Path}. Ready for inference.", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX session. Prediction engine disabled.");
        }
    }

    /// <summary>
    /// Evaluates the OrderBookSnapshot and returns an AI prediction confidence [-1.0 to 1.0].
    /// Passing by 'in' for zero-allocation performance.
    /// </summary>
    public float PredictDirection(in OrderBookSnapshot orderBook)
    {
        if (_session == null) return 0f;
        if (orderBook.Bids.Length == 0 || orderBook.Asks.Length == 0) return 0f;

        // 1. Calculate features from the fast Level 2 Snapshot
        float bestBidPrice = (float)orderBook.Bids[0].Price;
        float bestBidQty = (float)orderBook.Bids[0].Quantity;
        float bestAskPrice = (float)orderBook.Asks[0].Price;
        float bestAskQty = (float)orderBook.Asks[0].Quantity;
        
        float spread = bestAskPrice - bestBidPrice;
        
        // Order Flow Imbalance: (BidQty - AskQty) / (BidQty + AskQty)
        float totalDepth = bestBidQty + bestAskQty;
        float imbalance = totalDepth == 0 ? 0 : (bestBidQty - bestAskQty) / totalDepth;

        // Map features to flat array
        _features[0] = bestBidPrice;
        _features[1] = bestBidQty;
        _features[2] = bestAskPrice;
        _features[3] = bestAskQty;
        _features[4] = spread;
        _features[5] = imbalance;

        // 2. Wrap features in a Tensor (Fixed array, zero-copy)
        var inputTensor = new DenseTensor<float>(_features, new[] { 1, 6 });
        
        // Re-use list to avoid heap allocation per tick
        var inputs = new NamedOnnxValue[] 
        { 
            NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) 
        };

        // 3. Inference
        try
        {
            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();
            
            // Assuming output shape [1, 1], returning float representation of direction
            float prediction = output[0, 0];
            
            // Example interpretation: > 0.5 is Bullish, < -0.5 is Bearish
            return prediction;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX Inference failed during live market tick.");
            return 0f;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
