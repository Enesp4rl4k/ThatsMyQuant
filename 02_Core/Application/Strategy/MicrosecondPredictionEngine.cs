using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

public class MicrosecondPredictionEngine : IDisposable
{
    private readonly ILogger<MicrosecondPredictionEngine> _logger;
    private readonly IMediator? _mediator;
    private readonly InferenceSession? _session;
    private readonly string _inputName;

    private readonly Queue<float> _vpinHistory = new();
    private const int MaxVpinHistory = 1000;

    public MicrosecondPredictionEngine(string modelPath, ILogger<MicrosecondPredictionEngine> logger, IMediator? mediator = null)
    {
        _logger = logger;
        _mediator = mediator;
        
        try
        {
            if (string.IsNullOrEmpty(modelPath) || !System.IO.File.Exists(modelPath))
            {
                _logger.LogWarning("ONNX model file not found at {Path}. Prediction engine disabled.", modelPath);
                _inputName = "input";
                return;
            }

            var sessionOptions = new SessionOptions();
            _session = new InferenceSession(modelPath, sessionOptions);
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "input";
            
            _logger.LogInformation("ONNX ML Model loaded from {Path}.", modelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize ONNX session.");
            _inputName = "input";
        }
    }

    public float PredictDirection(in OrderBookSnapshot orderBook)
    {
        // SonarQube Fix: Oturum yoksa veya veri boşsa erken çıkış
        if (_session == null || orderBook.Bids.Length == 0 || orderBook.Asks.Length == 0) return 0f;

        // SonarQube Fix (Thread Safety): 
        // Her çağrı için yerel bir array kullanıyoruz ki thread'ler birbirinin verisini bozmasın.
        // Stack-allocated (stackalloc) kullanarak performansı koruyabiliriz ama DenseTensor 
        // managed array beklediği için en güvenlisi yerel array'dir.
        float[] features = new float[8];
        
        CalculateFeatures(orderBook, features);
        EvaluateVpinToxicity(orderBook.Symbol, features[7]);

        try
        {
            // Inference için gerekli nesneleri yerel (local) oluşturuyoruz
            var inputTensor = new DenseTensor<float>(features, new[] { 1, 8 });
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };

            using var results = _session.Run(inputs);
            var output = results.First().AsTensor<float>();
            
            return output[0, 0];
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
        float imbalance = totalDepth < 1e-9 ? 0 : (bestBidQty - bestAskQty) / totalDepth;

        var (totalBidVol, totalAskVol) = CalculateDepthVolumes(orderBook, 5);
        
        float totalVolume = totalBidVol + totalAskVol;
        float voi = totalVolume < 1e-9 ? 0 : (totalBidVol - totalAskVol) / totalVolume;
        float vpinProxy = totalVolume < 1e-9 ? 0 : Math.Abs(totalBidVol - totalAskVol) / totalVolume;

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

    private void EvaluateVpinToxicity(string symbol, float currentVpin)
    {
        if (_mediator == null) return;
        
        lock (_vpinHistory)
        {
            if (_vpinHistory.Count >= MaxVpinHistory)
                _vpinHistory.Dequeue();
            _vpinHistory.Enqueue(currentVpin);

            if (_vpinHistory.Count < 50) return; // Need minimum sample size

            var mean = _vpinHistory.Average();
            var sumOfSquaresOfDifferences = _vpinHistory.Select(val => (val - mean) * (val - mean)).Sum();
            var standardDeviation = Math.Sqrt(sumOfSquaresOfDifferences / _vpinHistory.Count);

            if (standardDeviation > 1e-9)
            {
                var zScore = (currentVpin - mean) / standardDeviation;
                
                // > 2.0 Z-Score indicates statistical extreme anomaly (~95th percentile)
                if (zScore > 2.0)
                {
                    _logger.LogWarning("Toxic order flow detected for {Symbol}. VPIN Z-Score: {ZScore:F2}", symbol, zScore);
                    
                    // Fire-and-forget notification to prevent blocking the HFT execution thread
                    _ = _mediator.Publish(new VpinToxicityAlert(symbol, zScore, DateTime.UtcNow));
                }
            }
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        GC.SuppressFinalize(this); // SonarQube standard: Dispose implement ediliyorsa bu eklenmeli.
    }
}
