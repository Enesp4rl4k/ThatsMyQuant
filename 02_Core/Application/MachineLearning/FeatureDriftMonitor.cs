using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Online feature distribution drift detector using Page-Hinkley change detection.
/// Monitors each feature dimension independently, maintaining running statistics and
/// triggering alerts when the cumulative deviation from the training-time mean exceeds
/// a configurable threshold.
///
/// Page-Hinkley is preferred over Kolmogorov-Smirnov for streaming data because:
///  - O(1) per observation (no windowed sample storage)
///  - Detects both gradual drift and sudden shifts
///  - Tunable sensitivity via delta (minimum magnitude of change to detect)
///    and lambda (detection threshold)
///
/// Usage:
///   1. Call <see cref="SetBaseline"/> with training-time feature statistics.
///   2. Call <see cref="Observe"/> on each inference feature vector.
///   3. Query <see cref="GetDriftReport"/> to check which features have drifted.
/// </summary>
public sealed class FeatureDriftMonitor
{
    private readonly ILogger<FeatureDriftMonitor> _logger;
    private readonly int _featureDimensions;
    private readonly FeatureTracker[] _trackers;
    private readonly string[] _featureNames;
    private readonly object _syncLock = new();
    private long _totalObservations;

    // Page-Hinkley parameters
    private readonly double _delta;   // Minimum magnitude of change to detect (tolerance)
    private readonly double _lambda;  // Detection threshold — lower = more sensitive

    /// <summary>
    /// Fires when any feature dimension exceeds the drift threshold.
    /// </summary>
    public event Action<DriftAlert>? OnDriftDetected;

    public FeatureDriftMonitor(
        ILogger<FeatureDriftMonitor> logger,
        int featureDimensions = 5,
        double delta = 0.005,
        double lambda = 50.0,
        string[]? featureNames = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _featureDimensions = featureDimensions;
        _delta = delta;
        _lambda = lambda;
        _featureNames = featureNames ?? DefaultFeatureNames(featureDimensions);
        _trackers = new FeatureTracker[featureDimensions];

        for (int i = 0; i < featureDimensions; i++)
            _trackers[i] = new FeatureTracker();
    }

    /// <summary>
    /// Sets the baseline distribution from training data. Call once after model training.
    /// </summary>
    public void SetBaseline(double[] means, double[] stddevs)
    {
        if (means.Length != _featureDimensions || stddevs.Length != _featureDimensions)
            throw new ArgumentException($"Expected {_featureDimensions} dimensions, got means={means.Length}, stddevs={stddevs.Length}");

        for (int i = 0; i < _featureDimensions; i++)
        {
            _trackers[i].BaselineMean = means[i];
            _trackers[i].BaselineStddev = stddevs[i] > 0 ? stddevs[i] : 1.0;
            _trackers[i].Reset();
        }

        _totalObservations = 0;
        _logger.LogInformation("[DriftMonitor] Baseline set for {Dims} features", _featureDimensions);
    }

    /// <summary>
    /// Computes baseline statistics from a batch of training feature vectors.
    /// </summary>
    public void SetBaselineFromData(IReadOnlyList<float[]> trainingVectors)
    {
        if (trainingVectors.Count < 10)
            throw new ArgumentException("Need at least 10 vectors to compute baseline statistics");

        var means = new double[_featureDimensions];
        var m2 = new double[_featureDimensions];

        // Welford's online algorithm for mean + variance
        for (int n = 0; n < trainingVectors.Count; n++)
        {
            var vec = trainingVectors[n];
            for (int d = 0; d < _featureDimensions && d < vec.Length; d++)
            {
                double delta = vec[d] - means[d];
                means[d] += delta / (n + 1);
                double delta2 = vec[d] - means[d];
                m2[d] += delta * delta2;
            }
        }

        var stddevs = new double[_featureDimensions];
        for (int d = 0; d < _featureDimensions; d++)
            stddevs[d] = Math.Sqrt(Math.Max(0, m2[d] / trainingVectors.Count));

        SetBaseline(means, stddevs);
    }

    /// <summary>
    /// Observes a new feature vector from live inference. O(1) per call.
    /// Returns true if any feature has drifted beyond the threshold.
    /// </summary>
    public bool Observe(float[] featureVector)
    {
        if (featureVector.Length != _featureDimensions) return false;

        lock (_syncLock)
        {
            _totalObservations++;
            bool anyDrift = false;

            for (int i = 0; i < _featureDimensions; i++)
            {
                double value = featureVector[i];
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;

                var tracker = _trackers[i];
                if (tracker.BaselineStddev <= 0) continue;

                // Normalize observation relative to baseline
                double normalized = (value - tracker.BaselineMean) / tracker.BaselineStddev;

                // Page-Hinkley update
                tracker.CumulativeSum += normalized - _delta;
                if (tracker.CumulativeSum < tracker.MinCumulativeSum)
                    tracker.MinCumulativeSum = tracker.CumulativeSum;

                double phStat = tracker.CumulativeSum - tracker.MinCumulativeSum;

                // Also track negative direction
                tracker.CumulativeSumNeg += -normalized - _delta;
                if (tracker.CumulativeSumNeg < tracker.MinCumulativeSumNeg)
                    tracker.MinCumulativeSumNeg = tracker.CumulativeSumNeg;

                double phStatNeg = tracker.CumulativeSumNeg - tracker.MinCumulativeSumNeg;

                // Update online mean/variance for live distribution
                tracker.LiveCount++;
                double liveDelta = value - tracker.LiveMean;
                tracker.LiveMean += liveDelta / tracker.LiveCount;
                double liveDelta2 = value - tracker.LiveMean;
                tracker.LiveM2 += liveDelta * liveDelta2;

                bool wasDrifted = tracker.IsDrifted;
                tracker.IsDrifted = phStat > _lambda || phStatNeg > _lambda;
                tracker.PageHinkleyStat = Math.Max(phStat, phStatNeg);

                if (tracker.IsDrifted && !wasDrifted)
                {
                    anyDrift = true;
                    var alert = new DriftAlert
                    {
                        FeatureIndex = i,
                        FeatureName = _featureNames[i],
                        PageHinkleyStat = tracker.PageHinkleyStat,
                        Threshold = _lambda,
                        BaselineMean = tracker.BaselineMean,
                        BaselineStddev = tracker.BaselineStddev,
                        LiveMean = tracker.LiveMean,
                        LiveStddev = tracker.LiveCount > 1 ? Math.Sqrt(Math.Max(0, tracker.LiveM2 / tracker.LiveCount)) : 0,
                        ObservationCount = _totalObservations
                    };

                    _logger.LogWarning(
                        "[DriftMonitor] DRIFT DETECTED on Feature[{Index}] ({Name}): PH={Stat:F2} > λ={Lambda:F2} | " +
                        "Baseline μ={BM:F4} σ={BS:F4} → Live μ={LM:F4} σ={LS:F4}",
                        i, _featureNames[i], tracker.PageHinkleyStat, _lambda,
                        tracker.BaselineMean, tracker.BaselineStddev,
                        alert.LiveMean, alert.LiveStddev);

                    OnDriftDetected?.Invoke(alert);
                }
            }

            return anyDrift;
        }
    }

    /// <summary>
    /// Returns the current drift status for all features.
    /// </summary>
    public DriftReport GetDriftReport()
    {
        lock (_syncLock)
        {
            var features = new List<FeatureDriftStatus>();
            for (int i = 0; i < _featureDimensions; i++)
            {
                var t = _trackers[i];
                features.Add(new FeatureDriftStatus
                {
                    FeatureIndex = i,
                    FeatureName = _featureNames[i],
                    IsDrifted = t.IsDrifted,
                    PageHinkleyStat = t.PageHinkleyStat,
                    BaselineMean = t.BaselineMean,
                    BaselineStddev = t.BaselineStddev,
                    LiveMean = t.LiveMean,
                    LiveStddev = t.LiveCount > 1 ? Math.Sqrt(Math.Max(0, t.LiveM2 / t.LiveCount)) : 0,
                    LiveObservations = t.LiveCount
                });
            }

            return new DriftReport
            {
                TotalObservations = _totalObservations,
                DriftedFeatureCount = features.Count(f => f.IsDrifted),
                Features = features,
                OverallHealthy = features.All(f => !f.IsDrifted)
            };
        }
    }

    /// <summary>
    /// Resets all trackers. Call after model retraining to establish a new baseline.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _featureDimensions; i++)
            _trackers[i].Reset();
        _totalObservations = 0;
    }

    private static string[] DefaultFeatureNames(int dims) => dims switch
    {
        5 => new[] { "RSI_Norm", "EMA_Deviation", "Realized_Volatility", "OrderFlow_Imbalance", "Price_Momentum" },
        _ => Enumerable.Range(0, dims).Select(i => $"Feature_{i}").ToArray()
    };

    /// <summary>
    /// Per-feature Page-Hinkley tracker with online statistics.
    /// </summary>
    private sealed class FeatureTracker
    {
        public double BaselineMean;
        public double BaselineStddev = 1.0;

        // Page-Hinkley state (positive direction)
        public double CumulativeSum;
        public double MinCumulativeSum;

        // Page-Hinkley state (negative direction)
        public double CumulativeSumNeg;
        public double MinCumulativeSumNeg;

        public double PageHinkleyStat;
        public bool IsDrifted;

        // Online live distribution (Welford's)
        public long LiveCount;
        public double LiveMean;
        public double LiveM2;

        public void Reset()
        {
            CumulativeSum = 0;
            MinCumulativeSum = 0;
            CumulativeSumNeg = 0;
            MinCumulativeSumNeg = 0;
            PageHinkleyStat = 0;
            IsDrifted = false;
            LiveCount = 0;
            LiveMean = 0;
            LiveM2 = 0;
        }
    }
}

public record DriftAlert
{
    public int FeatureIndex { get; init; }
    public string FeatureName { get; init; } = "";
    public double PageHinkleyStat { get; init; }
    public double Threshold { get; init; }
    public double BaselineMean { get; init; }
    public double BaselineStddev { get; init; }
    public double LiveMean { get; init; }
    public double LiveStddev { get; init; }
    public long ObservationCount { get; init; }
}

public record DriftReport
{
    public long TotalObservations { get; init; }
    public int DriftedFeatureCount { get; init; }
    public bool OverallHealthy { get; init; }
    public List<FeatureDriftStatus> Features { get; init; } = new();
}

public record FeatureDriftStatus
{
    public int FeatureIndex { get; init; }
    public string FeatureName { get; init; } = "";
    public bool IsDrifted { get; init; }
    public double PageHinkleyStat { get; init; }
    public double BaselineMean { get; init; }
    public double BaselineStddev { get; init; }
    public double LiveMean { get; init; }
    public double LiveStddev { get; init; }
    public long LiveObservations { get; init; }
}
