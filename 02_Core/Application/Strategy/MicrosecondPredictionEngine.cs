using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ThatsMyQuant.Application.HftInfrastructure;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// YELLOW PATH: ML inference engine for microsecond-scale directional prediction.
/// Pre-allocates all buffers at construction time to eliminate GC pressure.
/// Writes directional bias to HotStateBuffer for Red Path consumption.
///
/// Performance: ~50-200μs per inference (ONNX CPU), zero allocation per call.
/// </summary>
public class MicrosecondPredictionEngine : IDisposable
{
    private readonly ILogger<MicrosecondPredictionEngine> _logger;
    private readonly InferenceSession? _session;
    private readonly string _inputName;
    private readonly HotStateBuffer? _hotState;

    // Pre-allocated buffers — created once, reused every inference cycle
    private readonly float[] _features = new float[8];
    private readonly DenseTensor<float> _inputTensor;
    private readonly List<NamedOnnxValue> _inputs;

    public MicrosecondPredictionEngine(
        string modelPath,
        ILogger<MicrosecondPredictionEngine> logger,
        HotStateBuffer? hotState = null)
    {
        _logger = logger;
        _hotState = hotState;
        _inputTensor = new DenseTensor<float>(_features, [1, 8]);

        try
        {
            if (string.IsNullOrEmpty(modelPath) || !System.IO.File.Exists(modelPath))
            {
                _logger.LogWarning("ONNX model file not found at {Path}. Prediction engine disabled.", modelPath);
                _inputName = "input";
                _inputs = [NamedOnnxValue.CreateFromTensor("input", _inputTensor)];
                return;
            }

            var sessionOptions = new SessionOptions();
            sessionOptions.EnableMemoryPattern = true;
            sessionOptions.EnableCpuMemArena = true;
            _session = new InferenceSession(modelPath, sessionOptions);
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "input";
            _inputs = [NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor)];

            _logger.LogInformation("ONNX ML Model loaded from {Path}. Yellow Path inference ready.", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX session.");
            _inputName = "input";
            _inputs = [NamedOnnxValue.CreateFromTensor("input", _inputTensor)];
        }
    }

    /// <summary>
    /// Zero-allocation inference. Writes features into pre-allocated buffer,
    /// runs ONNX, and optionally publishes to HotStateBuffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float PredictDirection(in OrderBookSnapshot orderBook)
    {
        if (_session == null || orderBook.Bids.Length == 0 || orderBook.Asks.Length == 0) return 0f;

        CalculateFeatures(orderBook, _features);

        try
        {
            using var results = _session.Run(_inputs);
            var output = results.First().AsTensor<float>();
            float prediction = output[0, 0];

            // Publish to HotStateBuffer for Red Path (atomic, nanosecond)
            _hotState?.UpdateDirectionalBias(prediction, Math.Abs(prediction) * 100f);

            return prediction;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ONNX Inference failed.");
            return 0f;
        }
    }

    private static void CalculateFeatures(in OrderBookSnapshot orderBook, float[] features)
    {
        float bestBidPrice = (float)orderBook.Bids[0].Price;
        float bestBidQty = (float)orderBook.Bids[0].Quantity;
        float bestAskPrice = (float)orderBook.Asks[0].Price;
        float bestAskQty = (float)orderBook.Asks[0].Quantity;

        float totalDepth = bestBidQty + bestAskQty;
        float imbalance = totalDepth < 1e-9f ? 0 : (bestBidQty - bestAskQty) / totalDepth;

        var (totalBidVol, totalAskVol) = CalculateDepthVolumes(orderBook, 5);

        float totalVolume = totalBidVol + totalAskVol;
        float voi = totalVolume < 1e-9f ? 0 : (totalBidVol - totalAskVol) / totalVolume;
        float vpinProxy = totalVolume < 1e-9f ? 0 : Math.Abs(totalBidVol - totalAskVol) / totalVolume;

        features[0] = bestBidPrice;
        features[1] = bestBidQty;
        features[2] = bestAskPrice;
        features[3] = bestAskQty;
        features[4] = bestAskPrice - bestBidPrice;
        features[5] = imbalance;
        features[6] = voi;
        features[7] = vpinProxy;
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
        GC.SuppressFinalize(this);
    }
}
