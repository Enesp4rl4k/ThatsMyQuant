using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using ThatsMyQuant.Application.Observability;
using ThatsMyQuant.Contracts.Options;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Strictly typed output matrix replacing ambiguous '0f' boolean fallsbacks.
/// Futureproofed to carry confidence projections and Return targets over Price.
/// </summary>
public struct PredictionResult
{
    public float Value;
    public bool IsValid;
    public float Confidence; // Distance mapping for stochastic thresholds
    public string ModelVersion;
    public bool DriftDetected;
    public bool DriftIsCritical;
    public int DriftedFeatureCount;
    public double MaxFeatureZScore;
}

/// <summary>
/// The ultra-low latency execution component of the ML pipeline.
/// Pool of ML.NET prediction engines with optional lazy background fill (see <see cref="InferenceOptions.LazyPoolWarmup"/>).
/// </summary>
public class InferenceAgent
{
    private readonly ConcurrentStack<(PredictionEngine<CandleMlData, CandlePrediction> Engine, CandleMlData Buffer)> _pool = new();
    private readonly int _targetPoolSize;
    private readonly MLContext _mlContext;
    private readonly ITransformer? _loadedModel;
    private readonly InferenceOptions _options;
    private readonly bool _lazyPoolWarmup;
    private readonly FeatureDriftMonitor? _featureDriftMonitor;
    private readonly string _modelVersion;
    private readonly ILogger<InferenceAgent>? _logger;
    private readonly OperationalTelemetry? _telemetry;
    private readonly LogLevel _lazyPoolBackgroundLogLevel;

    private int _totalEnginesBuilt;
    private int _backgroundExpansionStarted;
    private readonly object _poolGrowthLock = new();
    private readonly bool _modelAvailable;

    public string ActiveModelVersion => _modelVersion;
    public bool IsModelAvailable => _modelAvailable;

    public InferenceAgent()
        : this((FeatureDriftMonitor?)null, "PredictiveAlphaModel.zip", null)
    {
    }

    public InferenceAgent(string modelPath)
        : this((FeatureDriftMonitor?)null, modelPath, null)
    {
    }

    public InferenceAgent(FeatureDriftMonitor featureDriftMonitor)
        : this(featureDriftMonitor, "PredictiveAlphaModel.zip", null)
    {
    }

    public InferenceAgent(
        FeatureDriftMonitor? featureDriftMonitor,
        string modelPath,
        InferenceOptions? options = null,
        ILogger<InferenceAgent>? logger = null,
        OperationalTelemetry? telemetry = null)
    {
        _mlContext = new MLContext(seed: 42);
        _featureDriftMonitor = featureDriftMonitor;
        _modelVersion = BuildModelVersion(modelPath);
        _options = options ?? new InferenceOptions();
        _lazyPoolWarmup = _options.LazyPoolWarmup;
        _targetPoolSize = _options.GetEffectivePoolSize();
        _logger = logger;
        _telemetry = telemetry;
        _lazyPoolBackgroundLogLevel = ParseLazyPoolBackgroundLogLevel(_options.LazyPoolBackgroundLogLevel);

        if (!File.Exists(modelPath))
        {
            _modelAvailable = false;
            _logger?.LogWarning("Inference model not found at {ModelPath}. ML inference will run in disabled mode.", modelPath);
            _telemetry?.SetInferencePoolTelemetry(0, 0, true);
            return;
        }

        _loadedModel = _mlContext.Model.Load(modelPath, out var _);
        _modelAvailable = true;

        if (!_lazyPoolWarmup)
        {
            EagerWarmupFullPool();
            _telemetry?.SetInferencePoolTelemetry(_targetPoolSize, _targetPoolSize, true);
        }
        else
        {
            _telemetry?.SetInferencePoolTelemetry(0, _targetPoolSize, false);
        }
    }

    private static LogLevel ParseLazyPoolBackgroundLogLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return LogLevel.Warning;
        return Enum.TryParse<LogLevel>(value.Trim(), ignoreCase: true, out var level) ? level : LogLevel.Warning;
    }

    /// <summary>
    /// Startup path: build the full pool in the constructor (highest startup cost, best first-second concurrency).
    /// </summary>
    private void EagerWarmupFullPool()
    {
        if (!_modelAvailable || _loadedModel is null)
            return;

        void PushOne()
        {
            var engine = _mlContext.Model.CreatePredictionEngine<CandleMlData, CandlePrediction>(_loadedModel);
            var dummyData = new CandleMlData { Features = new float[5] };
            engine.Predict(dummyData);
            _pool.Push((engine, new CandleMlData()));
            Interlocked.Increment(ref _totalEnginesBuilt);
        }

        if (_options.UseParallelWarmup)
            Parallel.For(0, _targetPoolSize, _ => PushOne());
        else
        {
            for (var i = 0; i < _targetPoolSize; i++)
                PushOne();
        }
    }

    /// <summary>
    /// Lazy path: guarantee at least one engine exists, then (once) fill the rest on a thread-pool thread.
    /// </summary>
    private void EnsureLazyBootstrapAndBackgroundFill()
    {
        if (!_modelAvailable || _loadedModel is null)
            return;

        lock (_poolGrowthLock)
        {
            if (_pool.TryPop(out var existing))
            {
                _pool.Push(existing);
                return;
            }

            if (_totalEnginesBuilt < _targetPoolSize)
                TryPushOneEngineUnderLock();

            StartBackgroundExpansionOnce();
        }
    }

    private void TryPushOneEngineUnderLock()
    {
        if (!_modelAvailable || _loadedModel is null)
            return;

        if (_totalEnginesBuilt >= _targetPoolSize)
            return;

        var engine = _mlContext.Model.CreatePredictionEngine<CandleMlData, CandlePrediction>(_loadedModel);
        var dummyData = new CandleMlData { Features = new float[5] };
        engine.Predict(dummyData);
        _pool.Push((engine, new CandleMlData()));
        _totalEnginesBuilt++;
        _telemetry?.SetInferencePoolTelemetry(_totalEnginesBuilt, _targetPoolSize, _totalEnginesBuilt >= _targetPoolSize);
    }

    private void StartBackgroundExpansionOnce()
    {
        if (!_lazyPoolWarmup || _totalEnginesBuilt >= _targetPoolSize)
            return;
        if (Interlocked.CompareExchange(ref _backgroundExpansionStarted, 1, 0) != 0)
            return;

        var target = _targetPoolSize;
        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    lock (_poolGrowthLock)
                    {
                        if (_totalEnginesBuilt >= target)
                        {
                            _logger?.LogInformation(
                                "Inference lazy pool background fill completed: {Engines} engines (target {Target}).",
                                _totalEnginesBuilt,
                                target);
                            return;
                        }

                        TryPushOneEngineUnderLock();
                    }
                }
            }
            catch (Exception ex)
            {
                _telemetry?.RecordInferenceLazyPoolBackgroundFillFailure();
                if (_lazyPoolBackgroundLogLevel != LogLevel.None)
                {
                    int built;
                    lock (_poolGrowthLock)
                        built = _totalEnginesBuilt;
                    _logger?.Log(
                        _lazyPoolBackgroundLogLevel,
                        ex,
                        "Inference lazy pool background fill failed after building {EnginesBuilt} of {Target} engines.",
                        built,
                        target);
                }
            }
        });
    }

    /// <summary>
    /// Predicts the target trajectory using the realtime features from the agent.
    /// Operates using a concurrent pool and zero-allocation techniques.
    /// </summary>
    public PredictionResult PredictNextReturn(FeatureEngineeringAgent featureAgent)
    {
        if (!_modelAvailable || _loadedModel is null)
            return new PredictionResult { Value = 0f, IsValid = false, ModelVersion = _modelVersion };
        if (featureAgent == null) return new PredictionResult { Value = 0f, IsValid = false };

        if (!_pool.TryPop(out var item))
        {
            if (_lazyPoolWarmup)
                EnsureLazyBootstrapAndBackgroundFill();
            if (!_pool.TryPop(out item))
                return new PredictionResult { Value = 0f, IsValid = false, ModelVersion = _modelVersion };
        }

        try
        {
            featureAgent.CopyTo(item.Buffer.Features);
            return PredictInternal(item.Buffer, item.Engine);
        }
        finally
        {
            _pool.Push(item);
        }
    }

    [Obsolete("Use PredictNextReturn(FeatureEngineeringAgent) for better performance.")]
    public PredictionResult PredictNextReturn(float[] currentNormalizedFeatures)
    {
        if (!_modelAvailable || _loadedModel is null)
            return new PredictionResult { Value = 0f, IsValid = false, ModelVersion = _modelVersion };
        if (currentNormalizedFeatures == null || currentNormalizedFeatures.Length != 5)
            return new PredictionResult { Value = 0f, IsValid = false };

        if (!_pool.TryPop(out var item))
        {
            if (_lazyPoolWarmup)
                EnsureLazyBootstrapAndBackgroundFill();
            if (!_pool.TryPop(out item))
                return new PredictionResult { Value = 0f, IsValid = false, ModelVersion = _modelVersion };
        }

        try
        {
            currentNormalizedFeatures.CopyTo(item.Buffer.Features, 0);
            return PredictInternal(item.Buffer, item.Engine);
        }
        finally
        {
            _pool.Push(item);
        }
    }

    private PredictionResult PredictInternal(CandleMlData buffer, PredictionEngine<CandleMlData, CandlePrediction> engine)
    {
        for (int i = 0; i < 5; i++)
        {
            if (float.IsNaN(buffer.Features[i]) || float.IsInfinity(buffer.Features[i]))
            {
                return new PredictionResult { Value = 0f, IsValid = false };
            }
        }

        var driftReport = _featureDriftMonitor?.UpdateAll(buffer.Features);
        var driftDetected = driftReport is { DriftedCount: > 0 };
        if (driftReport is { IsCritical: true })
        {
            return new PredictionResult
            {
                Value = 0f,
                IsValid = false,
                ModelVersion = _modelVersion,
                DriftDetected = true,
                DriftIsCritical = true,
                DriftedFeatureCount = driftReport.DriftedCount,
                MaxFeatureZScore = driftReport.MaxZScore
            };
        }

        var prediction = engine.Predict(buffer);

        return new PredictionResult
        {
            Value = prediction.PredictedNextClose,
            IsValid = true,
            Confidence = 0.99f,
            ModelVersion = _modelVersion,
            DriftDetected = driftDetected,
            DriftIsCritical = false,
            DriftedFeatureCount = driftReport?.DriftedCount ?? 0,
            MaxFeatureZScore = driftReport?.MaxZScore ?? 0
        };
    }

    private static string BuildModelVersion(string modelPath)
    {
        var file = new FileInfo(modelPath);
        var stamp = file.Exists ? file.LastWriteTimeUtc : DateTime.UtcNow;
        return $"{Path.GetFileNameWithoutExtension(modelPath)}@{stamp:yyyyMMddHHmmss}";
    }
}
