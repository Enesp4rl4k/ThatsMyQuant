using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Online feature drift monitor using a training baseline plus Page-Hinkley change detection.
/// This lets the engine detect both long-running distribution shift and abrupt deviation from the
/// feature distribution the model was trained on.
/// </summary>
public sealed class FeatureDriftMonitor
{
    private readonly ILogger<FeatureDriftMonitor>? _logger;
    private readonly int _featureCount;
    private readonly double _lambda;
    private readonly double _alpha;
    private readonly double _zScoreThreshold;
    private readonly int _minimumSamplesBeforeDrift;

    private readonly double[] _runningMean;
    private readonly double[] _runningVariance;
    private readonly double[] _cumulativeSum;
    private readonly double[] _minCumulativeSum;
    private readonly long[] _sampleCount;
    private readonly bool[] _driftDetected;
    private readonly double[] _baselineMean;
    private readonly double[] _baselineStdDev;
    private readonly bool[] _baselineConfigured;
    private readonly double[] _lastZScore;

    /// <summary>
    /// Maximum number of samples before the running statistics are decayed.
    /// Prevents infinite accumulation and keeps drift detection responsive to recent data.
    /// When the sample count exceeds this window, an exponential decay factor is applied
    /// so the running mean/variance gradually forget old observations.
    /// </summary>
    private readonly long _maxWindowSize;

    public FeatureDriftMonitor(
        int featureCount,
        double lambda = 50.0,
        double alpha = 0.005,
        double zScoreThreshold = 3.0,
        int minimumSamplesBeforeDrift = 25,
        long maxWindowSize = 10_000,
        ILogger<FeatureDriftMonitor>? logger = null)
    {
        _featureCount = featureCount;
        _lambda = lambda;
        _alpha = alpha;
        _zScoreThreshold = zScoreThreshold;
        _minimumSamplesBeforeDrift = minimumSamplesBeforeDrift;
        _maxWindowSize = maxWindowSize > 0 ? maxWindowSize : 10_000;
        _logger = logger;

        _runningMean = new double[featureCount];
        _runningVariance = new double[featureCount];
        _cumulativeSum = new double[featureCount];
        _minCumulativeSum = new double[featureCount];
        _sampleCount = new long[featureCount];
        _driftDetected = new bool[featureCount];
        _baselineMean = new double[featureCount];
        _baselineStdDev = new double[featureCount];
        _baselineConfigured = new bool[featureCount];
        _lastZScore = new double[featureCount];
    }

    public bool HasBaseline => _baselineConfigured.Any(static configured => configured);

    public void SetBaseline(ReadOnlySpan<double> means, ReadOnlySpan<double> stdDevs)
    {
        var count = Math.Min(_featureCount, Math.Min(means.Length, stdDevs.Length));

        for (var i = 0; i < count; i++)
        {
            _baselineMean[i] = means[i];
            _baselineStdDev[i] = Math.Abs(stdDevs[i]) < 1e-6 ? 1.0 : Math.Abs(stdDevs[i]);
            _baselineConfigured[i] = true;
            _lastZScore[i] = 0.0;
        }
    }

    /// <summary>
    /// Updates the monitor with a new observation for a specific feature.
    /// Returns true if drift is detected for that feature.
    /// </summary>
    public bool Update(int featureIndex, double value)
    {
        if (featureIndex < 0 || featureIndex >= _featureCount)
        {
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        _sampleCount[featureIndex]++;
        var n = _sampleCount[featureIndex];

        // Sliding window decay: once we exceed the window size, apply exponential
        // forgetting so that old observations gradually lose influence.
        if (n > _maxWindowSize)
        {
            var decay = (double)_maxWindowSize / n;
            _runningVariance[featureIndex] *= decay;
            _cumulativeSum[featureIndex] *= decay;
            _minCumulativeSum[featureIndex] *= decay;
            _sampleCount[featureIndex] = _maxWindowSize;
            n = _maxWindowSize;
        }

        var delta = value - _runningMean[featureIndex];
        _runningMean[featureIndex] += delta / n;
        var delta2 = value - _runningMean[featureIndex];
        _runningVariance[featureIndex] += delta * delta2;

        _cumulativeSum[featureIndex] += value - _runningMean[featureIndex] - _alpha;
        _minCumulativeSum[featureIndex] = Math.Min(_minCumulativeSum[featureIndex], _cumulativeSum[featureIndex]);

        var pageHinkleyDrifted =
            n >= _minimumSamplesBeforeDrift &&
            (_cumulativeSum[featureIndex] - _minCumulativeSum[featureIndex]) > _lambda;

        var baselineDrifted = false;
        if (_baselineConfigured[featureIndex])
        {
            _lastZScore[featureIndex] =
                Math.Abs(value - _baselineMean[featureIndex]) / _baselineStdDev[featureIndex];
            baselineDrifted =
                n >= _minimumSamplesBeforeDrift &&
                _lastZScore[featureIndex] >= _zScoreThreshold;
        }

        var drifted = pageHinkleyDrifted || baselineDrifted;
        if (drifted && !_driftDetected[featureIndex])
        {
            _driftDetected[featureIndex] = true;
            _logger?.LogWarning(
                "Feature drift detected for feature[{Index}] after {Samples} samples. Mean={Mean:F4}, Variance={Variance:F4}, PHSum={PageHinkley:F4}, ZScore={ZScore:F2}",
                featureIndex,
                n,
                _runningMean[featureIndex],
                n > 1 ? _runningVariance[featureIndex] / (n - 1) : 0,
                _cumulativeSum[featureIndex] - _minCumulativeSum[featureIndex],
                _lastZScore[featureIndex]);
        }

        return drifted;
    }

    /// <summary>
    /// Updates all features from a tensor snapshot and returns a summary drift report.
    /// </summary>
    public DriftReport UpdateAll(ReadOnlySpan<float> features)
    {
        var driftCount = 0;
        var driftedIndices = new List<int>();
        var maxZScore = 0.0;

        var count = Math.Min(features.Length, _featureCount);
        for (var i = 0; i < count; i++)
        {
            if (Update(i, features[i]))
            {
                driftCount++;
                driftedIndices.Add(i);
            }

            maxZScore = Math.Max(maxZScore, _lastZScore[i]);
        }

        return new DriftReport
        {
            TotalFeatures = count,
            DriftedCount = driftCount,
            DriftedIndices = driftedIndices,
            MaxZScore = maxZScore,
            IsCritical = driftCount > count / 2 || maxZScore >= (_zScoreThreshold * 1.5)
        };
    }

    public FeatureStats GetStats(int featureIndex)
    {
        if (featureIndex < 0 || featureIndex >= _featureCount)
        {
            return new FeatureStats();
        }

        var n = _sampleCount[featureIndex];
        return new FeatureStats
        {
            Mean = _runningMean[featureIndex],
            Variance = n > 1 ? _runningVariance[featureIndex] / (n - 1) : 0,
            SampleCount = n,
            DriftDetected = _driftDetected[featureIndex],
            PageHinkleySum = _cumulativeSum[featureIndex] - _minCumulativeSum[featureIndex],
            BaselineConfigured = _baselineConfigured[featureIndex],
            BaselineMean = _baselineMean[featureIndex],
            BaselineStdDev = _baselineStdDev[featureIndex],
            LastZScore = _lastZScore[featureIndex]
        };
    }

    public void Reset(int featureIndex)
    {
        if (featureIndex < 0 || featureIndex >= _featureCount)
        {
            return;
        }

        _cumulativeSum[featureIndex] = 0;
        _minCumulativeSum[featureIndex] = 0;
        _driftDetected[featureIndex] = false;
        _lastZScore[featureIndex] = 0;
    }

    public void ResetAll()
    {
        for (var i = 0; i < _featureCount; i++)
        {
            Reset(i);
        }
    }
}

public sealed record DriftReport
{
    public int TotalFeatures { get; init; }
    public int DriftedCount { get; init; }
    public List<int> DriftedIndices { get; init; } = new();
    public bool IsCritical { get; init; }
    public double MaxZScore { get; init; }
}

public sealed record FeatureStats
{
    public double Mean { get; init; }
    public double Variance { get; init; }
    public long SampleCount { get; init; }
    public bool DriftDetected { get; init; }
    public double PageHinkleySum { get; init; }
    public bool BaselineConfigured { get; init; }
    public double BaselineMean { get; init; }
    public double BaselineStdDev { get; init; }
    public double LastZScore { get; init; }
}
