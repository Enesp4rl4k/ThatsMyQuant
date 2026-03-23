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

    // Feature array representing the orderbook: [BestBidPrice, BestBidQty, BestAskPrice, BestAskQty, Spread, ImbalanceRatio, VOI, VPIN]
    private readonly float[] _features = new float[8];
    private DenseTensor<float>? _inputTensor;
    private NamedOnnxValue[]? _inputs;

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
            
            _inputTensor = new DenseTensor<float>(_features, new[] { 1, 8 });
            _inputs = new NamedOnnxValue[] { NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor) };
            
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
        if (_session == null || orderBook.Bids.Length == 0 || orderBook.Asks.Length == 0) return 0f;

        // 1. Calculate features from the fast Level 2 Snapshot
        CalculateFeatures(orderBook);

        if (_inputs == null) return 0f;

        // 2. Inference
        try
        {
            using var results = _session.Run(_inputs);
            var output = results.First().AsTensor<float>();
            
            // Assuming output shape [1, 1], returning float representation of direction
            return output[0, 0];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX Inference failed during live market tick.");
            return 0f;
        }
    }

    private void CalculateFeatures(in OrderBookSnapshot orderBook)
    {
        float bestBidPrice = (float)orderBook.Bids[0].Price;
        float bestBidQty = (float)orderBook.Bids[0].Quantity;
        float bestAskPrice = (float)orderBook.Asks[0].Price;
        float bestAskQty = (float)orderBook.Asks[0].Quantity;
        
        float totalDepth = bestBidQty + bestAskQty;
        float imbalance = totalDepth == 0 ? 0 : (bestBidQty - bestAskQty) / totalDepth;

        // Calculate Level 2 VOI (Volume Order Imbalance) and VPIN proxy
        var (totalBidVol, totalAskVol) = CalculateDepthVolumes(orderBook, 5);
        
        float totalVolume = totalBidVol + totalAskVol;
        float voi = totalVolume == 0 ? 0 : (totalBidVol - totalAskVol) / totalVolume;
        
        // VPIN proxy (Volume-Synchronized Probability of Informed Trading)
        float vpinProxy = totalVolume == 0 ? 0 : Math.Abs(totalBidVol - totalAskVol) / totalVolume;

        // Map features to flat array. The _inputTensor uses this memory directly.
        _features[0] = bestBidPrice;
        _features[1] = bestBidQty;
        _features[2] = bestAskPrice;
        _features[3] = bestAskQty;
        _features[4] = bestAskPrice - bestBidPrice; // Spread
        _features[5] = imbalance;
        _features[6] = voi;
        _features[7] = vpinProxy;
    }

    private static (float totalBidVol, float totalAskVol) CalculateDepthVolumes(in OrderBookSnapshot orderBook, int maxLevels)
    {
        float totalBidVol = 0f;
        float totalAskVol = 0f;
        
        int levels = Math.Min(maxLevels, Math.Min(orderBook.Bids.Length, orderBook.Asks.Length));
        for (int i = 0; i < levels; i++)
        {
            totalBidVol += (float)orderBook.Bids[i].Quantity;
            totalAskVol += (float)orderBook.Asks[i].Quantity;
        }

        return (totalBidVol, totalAskVol);
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
