using System.Text.Json;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.MachineLearning;

/// <summary>
/// Local training pipeline for trade-outcome classification.
/// Collects labeled feature vectors, trains a standardized logistic model,
/// persists the active artifact, and exposes lightweight local inference.
/// </summary>
public sealed class ModelTrainingPipeline
{
    private const int FeatureCount = 5;
    private const int MinimumTrainableSamples = 50;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<ModelTrainingPipeline> _logger;
    private readonly FeatureEngineeringAgent _featureAgent;
    private readonly FeatureDriftMonitor? _featureDriftMonitor;
    private readonly List<LabeledSample> _samples = new();
    private readonly object _lock = new();
    private readonly string _artifactPath;

    private int _totalSamples;
    private int _positiveSamples;
    private TrainingModelArtifact? _activeModel;

    public int TotalSamples => _totalSamples;
    public int PositiveSamples => _positiveSamples;
    public double ClassBalance => _totalSamples > 0 ? (double)_positiveSamples / _totalSamples : 0.5;
    public bool HasActiveModel => _activeModel is not null;
    public string ArtifactPath => _artifactPath;

    public ModelTrainingPipeline(
        ILogger<ModelTrainingPipeline> logger,
        FeatureEngineeringAgent featureAgent,
        FeatureDriftMonitor? featureDriftMonitor = null,
        string? artifactDirectory = null)
    {
        _logger = logger;
        _featureAgent = featureAgent;
        _featureDriftMonitor = featureDriftMonitor;

        var modelDirectory = artifactDirectory ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ThatsMyQuant",
                "ml-models");

        Directory.CreateDirectory(modelDirectory);
        _artifactPath = Path.Combine(modelDirectory, "trade-outcome-model.json");

        TryLoadPersistedModel();
    }

    /// <summary>
    /// Captures the current feature tensor with a trade outcome label.
    /// Call this after a trade completes to build the training dataset.
    /// </summary>
    public void RecordSample(Trade trade, decimal pnl)
    {
        var features = new float[FeatureCount];
        _featureAgent.CopyTo(features.AsSpan());

        var sample = new LabeledSample
        {
            Features = SanitizeFeatures(features),
            Label = pnl > 0,
            Symbol = trade.Symbol,
            Timestamp = DateTime.UtcNow,
            PnL = pnl
        };

        lock (_lock)
        {
            _samples.Add(sample);
            _totalSamples++;
            if (sample.Label)
            {
                _positiveSamples++;
            }
        }

        _logger.LogDebug(
            "Recorded training sample #{Count}: {Symbol} -> {Label} (PnL: {PnL:F2})",
            _totalSamples,
            trade.Symbol,
            sample.Label ? "WIN" : "LOSS",
            pnl);
    }

    public IReadOnlyList<LabeledSample> GetDataset()
    {
        lock (_lock)
        {
            return _samples
                .Select(CloneSample)
                .ToList();
        }
    }

    public DatasetReport GetDatasetReport()
    {
        lock (_lock)
        {
            return new DatasetReport
            {
                TotalSamples = _totalSamples,
                PositiveSamples = _positiveSamples,
                NegativeSamples = _totalSamples - _positiveSamples,
                ClassBalance = ClassBalance,
                IsTrainable = _totalSamples >= MinimumTrainableSamples && ClassBalance > 0.1 && ClassBalance < 0.9,
                UniqueSymbols = _samples.Select(s => s.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                DateRange = _samples.Count > 0
                    ? (_samples.Min(s => s.Timestamp), _samples.Max(s => s.Timestamp))
                    : (DateTime.MinValue, DateTime.MinValue)
            };
        }
    }

    public TrainingResult Train(
        double trainSplitRatio = 0.8,
        int epochs = 250,
        double learningRate = 0.05,
        bool persistArtifact = true)
    {
        List<LabeledSample> dataset;
        lock (_lock)
        {
            dataset = _samples.Select(CloneSample).ToList();
        }

        if (dataset.Count < MinimumTrainableSamples)
        {
            _logger.LogWarning("Insufficient samples for training: {Count} < {Minimum}", dataset.Count, MinimumTrainableSamples);
            return new TrainingResult { Success = false, Reason = "Insufficient data" };
        }

        if (trainSplitRatio <= 0.5 || trainSplitRatio >= 0.95)
        {
            return new TrainingResult { Success = false, Reason = "Train split must be between 0.50 and 0.95." };
        }

        var split = CreateStratifiedSplit(dataset, trainSplitRatio);
        if (split.Train.Count == 0 || split.Test.Count == 0)
        {
            return new TrainingResult { Success = false, Reason = "Unable to create a valid stratified split." };
        }

        var normalization = ComputeNormalizationStats(split.Train);
        var trainRows = split.Train.Select(sample => CreateTrainingRow(sample, normalization)).ToList();
        var testRows = split.Test.Select(sample => CreateTrainingRow(sample, normalization)).ToList();

        var positives = trainRows.Count(row => row.Label);
        var negatives = trainRows.Count - positives;
        var positiveWeight = positives > 0 ? (double)negatives / positives : 1.0;
        var negativeWeight = negatives > 0 ? (double)positives / negatives : 1.0;

        var weights = new double[FeatureCount];
        double bias = 0.0;
        double previousLoss = double.MaxValue;
        const double l2Regularization = 0.0005;

        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var gradWeights = new double[FeatureCount];
            double gradBias = 0.0;
            double totalLoss = 0.0;

            foreach (var row in trainRows)
            {
                var probability = ScoreProbability(row.Features, weights, bias);
                var label = row.Label ? 1.0 : 0.0;
                var sampleWeight = row.Label ? positiveWeight : negativeWeight;
                var error = probability - label;

                gradBias += sampleWeight * error;
                for (var i = 0; i < FeatureCount; i++)
                {
                    gradWeights[i] += sampleWeight * error * row.Features[i];
                }

                totalLoss += sampleWeight * LogLoss(label, probability);
            }

            var scale = 1.0 / trainRows.Count;
            bias -= learningRate * gradBias * scale;

            for (var i = 0; i < FeatureCount; i++)
            {
                var regularizedGradient = (gradWeights[i] * scale) + (l2Regularization * weights[i]);
                weights[i] -= learningRate * regularizedGradient;
            }

            var averageLoss = totalLoss * scale;
            if (Math.Abs(previousLoss - averageLoss) < 0.000001)
            {
                previousLoss = averageLoss;
                break;
            }

            previousLoss = averageLoss;
        }

        var evaluation = Evaluate(testRows, weights, bias);
        var trainedAtUtc = DateTime.UtcNow;

        var artifact = new TrainingModelArtifact
        {
            Weights = weights,
            Bias = bias,
            FeatureMeans = normalization.Means,
            FeatureStdDevs = normalization.StdDevs,
            Accuracy = evaluation.Accuracy,
            Precision = evaluation.Precision,
            Recall = evaluation.Recall,
            F1Score = evaluation.F1Score,
            BalancedAccuracy = evaluation.BalancedAccuracy,
            LogLoss = evaluation.LogLoss,
            TrainSamples = trainRows.Count,
            TestSamples = testRows.Count,
            PositiveTrainSamples = positives,
            NegativeTrainSamples = negatives,
            TrainedAtUtc = trainedAtUtc
        };

        _activeModel = artifact;
        _featureDriftMonitor?.SetBaseline(artifact.FeatureMeans, artifact.FeatureStdDevs);

        if (persistArtifact)
        {
            PersistArtifact(artifact);
        }

        _logger.LogInformation(
            "Model trained: Accuracy={Accuracy:P1}, Precision={Precision:P1}, Recall={Recall:P1}, F1={F1:P1}, BalancedAccuracy={BalancedAccuracy:P1}, LogLoss={LogLoss:F4} ({TrainSize} train, {TestSize} test)",
            evaluation.Accuracy,
            evaluation.Precision,
            evaluation.Recall,
            evaluation.F1Score,
            evaluation.BalancedAccuracy,
            evaluation.LogLoss,
            trainRows.Count,
            testRows.Count);

        return new TrainingResult
        {
            Success = true,
            Accuracy = evaluation.Accuracy,
            Precision = evaluation.Precision,
            Recall = evaluation.Recall,
            F1Score = evaluation.F1Score,
            BalancedAccuracy = evaluation.BalancedAccuracy,
            LogLoss = evaluation.LogLoss,
            TrainSamples = trainRows.Count,
            TestSamples = testRows.Count,
            Weights = weights.ToArray(),
            Bias = bias,
            FeatureMeans = normalization.Means.ToArray(),
            FeatureStdDevs = normalization.StdDevs.ToArray(),
            ArtifactPath = _artifactPath,
            TrainedAtUtc = trainedAtUtc
        };
    }

    public bool TryPredictProbability(ReadOnlySpan<float> features, out double probability, out bool predictedPositive)
    {
        probability = 0.5;
        predictedPositive = false;

        if (_activeModel is null || features.Length < FeatureCount)
        {
            return false;
        }

        var sanitized = SanitizeFeatures(features);
        var driftReport = _featureDriftMonitor?.UpdateAll(sanitized);
        if (driftReport is { IsCritical: true })
        {
            _logger.LogWarning(
                "Critical feature drift invalidated local inference. Drifted={Drifted}/{Total}, MaxZ={MaxZ:F2}",
                driftReport.DriftedCount,
                driftReport.TotalFeatures,
                driftReport.MaxZScore);
            return false;
        }

        probability = ScoreProbability(
            NormalizeFeatures(sanitized, _activeModel.FeatureMeans, _activeModel.FeatureStdDevs),
            _activeModel.Weights,
            _activeModel.Bias);
        predictedPositive = probability >= 0.5;
        return true;
    }

    public bool TryLoadPersistedModel()
    {
        if (!File.Exists(_artifactPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_artifactPath);
            var artifact = JsonSerializer.Deserialize<TrainingModelArtifact>(json, JsonOptions);

            if (artifact is null ||
                artifact.Weights.Length != FeatureCount ||
                artifact.FeatureMeans.Length != FeatureCount ||
                artifact.FeatureStdDevs.Length != FeatureCount)
            {
                _logger.LogWarning("Persisted model artifact is invalid and was ignored: {ArtifactPath}", _artifactPath);
                return false;
            }

            _activeModel = artifact;
            _featureDriftMonitor?.SetBaseline(artifact.FeatureMeans, artifact.FeatureStdDevs);
            _logger.LogInformation("Loaded persisted trade outcome model from {ArtifactPath}", _artifactPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load persisted trade outcome model from {ArtifactPath}", _artifactPath);
            return false;
        }
    }

    public void ClearDataset()
    {
        lock (_lock)
        {
            _samples.Clear();
            _totalSamples = 0;
            _positiveSamples = 0;
        }
    }

    public void ClearPersistedModel()
    {
        _activeModel = null;
        if (File.Exists(_artifactPath))
        {
            File.Delete(_artifactPath);
        }
    }

    private void PersistArtifact(TrainingModelArtifact artifact)
    {
        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        File.WriteAllText(_artifactPath, json);
    }

    private static (List<LabeledSample> Train, List<LabeledSample> Test) CreateStratifiedSplit(
        IReadOnlyList<LabeledSample> dataset,
        double trainSplitRatio)
    {
        var random = new Random(42);
        var positive = dataset.Where(sample => sample.Label).OrderBy(_ => random.Next()).ToList();
        var negative = dataset.Where(sample => !sample.Label).OrderBy(_ => random.Next()).ToList();

        var positiveTrainCount = Math.Clamp((int)Math.Round(positive.Count * trainSplitRatio), 1, Math.Max(1, positive.Count - 1));
        var negativeTrainCount = Math.Clamp((int)Math.Round(negative.Count * trainSplitRatio), 1, Math.Max(1, negative.Count - 1));

        var train = positive.Take(positiveTrainCount)
            .Concat(negative.Take(negativeTrainCount))
            .OrderBy(_ => random.Next())
            .ToList();

        var test = positive.Skip(positiveTrainCount)
            .Concat(negative.Skip(negativeTrainCount))
            .OrderBy(_ => random.Next())
            .ToList();

        return (train, test);
    }

    private static NormalizationStats ComputeNormalizationStats(IReadOnlyList<LabeledSample> trainSet)
    {
        var means = new double[FeatureCount];
        var stdDevs = new double[FeatureCount];

        for (var i = 0; i < FeatureCount; i++)
        {
            means[i] = trainSet.Average(sample => sample.Features[i]);
            var variance = trainSet.Average(sample =>
            {
                var diff = sample.Features[i] - means[i];
                return diff * diff;
            });

            stdDevs[i] = Math.Sqrt(variance);
            if (stdDevs[i] < 1e-6)
            {
                stdDevs[i] = 1.0;
            }
        }

        return new NormalizationStats(means, stdDevs);
    }

    private static TrainingRow CreateTrainingRow(LabeledSample sample, NormalizationStats normalization)
        => new(NormalizeFeatures(sample.Features, normalization.Means, normalization.StdDevs), sample.Label);

    private static double[] NormalizeFeatures(IReadOnlyList<float> features, IReadOnlyList<double> means, IReadOnlyList<double> stdDevs)
    {
        var normalized = new double[FeatureCount];
        for (var i = 0; i < FeatureCount; i++)
        {
            normalized[i] = (features[i] - means[i]) / stdDevs[i];
        }

        return normalized;
    }

    private static double ScoreProbability(IReadOnlyList<double> normalizedFeatures, IReadOnlyList<double> weights, double bias)
    {
        var score = bias;
        for (var i = 0; i < FeatureCount; i++)
        {
            score += normalizedFeatures[i] * weights[i];
        }

        return Sigmoid(score);
    }

    private static double LogLoss(double label, double probability)
    {
        var clipped = Math.Clamp(probability, 1e-9, 1 - 1e-9);
        return -(label * Math.Log(clipped) + ((1 - label) * Math.Log(1 - clipped)));
    }

    private static EvaluationMetrics Evaluate(IReadOnlyList<TrainingRow> rows, IReadOnlyList<double> weights, double bias)
    {
        var tp = 0;
        var fp = 0;
        var tn = 0;
        var fn = 0;
        double totalLogLoss = 0.0;

        foreach (var row in rows)
        {
            var probability = ScoreProbability(row.Features, weights, bias);
            var predicted = probability >= 0.5;
            var actual = row.Label;

            if (predicted && actual) tp++;
            else if (predicted) fp++;
            else if (actual) fn++;
            else tn++;

            totalLogLoss += LogLoss(actual ? 1.0 : 0.0, probability);
        }

        var accuracy = rows.Count > 0 ? (double)(tp + tn) / rows.Count : 0.0;
        var precision = (tp + fp) > 0 ? (double)tp / (tp + fp) : 0.0;
        var recall = (tp + fn) > 0 ? (double)tp / (tp + fn) : 0.0;
        var specificity = (tn + fp) > 0 ? (double)tn / (tn + fp) : 0.0;
        var f1 = (precision + recall) > 0 ? (2 * precision * recall) / (precision + recall) : 0.0;
        var balancedAccuracy = (recall + specificity) / 2.0;
        var logLoss = rows.Count > 0 ? totalLogLoss / rows.Count : 0.0;

        return new EvaluationMetrics(accuracy, precision, recall, f1, balancedAccuracy, logLoss);
    }

    private static float[] SanitizeFeatures(ReadOnlySpan<float> features)
    {
        var sanitized = new float[FeatureCount];
        for (var i = 0; i < FeatureCount; i++)
        {
            sanitized[i] = FeatureEngineeringAgent.SafeFloat(features[i]);
        }

        return sanitized;
    }

    private static LabeledSample CloneSample(LabeledSample sample)
        => sample with { Features = sample.Features.ToArray() };

    private static double Sigmoid(double x)
        => 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -30, 30)));

    private sealed record NormalizationStats(double[] Means, double[] StdDevs);
    private sealed record TrainingRow(double[] Features, bool Label);
    private sealed record EvaluationMetrics(
        double Accuracy,
        double Precision,
        double Recall,
        double F1Score,
        double BalancedAccuracy,
        double LogLoss);
}

public sealed record LabeledSample
{
    public float[] Features { get; init; } = Array.Empty<float>();
    public bool Label { get; init; }
    public string Symbol { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public decimal PnL { get; init; }
}

public sealed record DatasetReport
{
    public int TotalSamples { get; init; }
    public int PositiveSamples { get; init; }
    public int NegativeSamples { get; init; }
    public double ClassBalance { get; init; }
    public bool IsTrainable { get; init; }
    public int UniqueSymbols { get; init; }
    public (DateTime Start, DateTime End) DateRange { get; init; }
}

public sealed record TrainingResult
{
    public bool Success { get; init; }
    public string Reason { get; init; } = "";
    public double Accuracy { get; init; }
    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1Score { get; init; }
    public double BalancedAccuracy { get; init; }
    public double LogLoss { get; init; }
    public int TrainSamples { get; init; }
    public int TestSamples { get; init; }
    public double[] Weights { get; init; } = Array.Empty<double>();
    public double[] FeatureMeans { get; init; } = Array.Empty<double>();
    public double[] FeatureStdDevs { get; init; } = Array.Empty<double>();
    public double Bias { get; init; }
    public string ArtifactPath { get; init; } = "";
    public DateTime TrainedAtUtc { get; init; }
}

public sealed record TrainingModelArtifact
{
    public double[] Weights { get; init; } = Array.Empty<double>();
    public double Bias { get; init; }
    public double[] FeatureMeans { get; init; } = Array.Empty<double>();
    public double[] FeatureStdDevs { get; init; } = Array.Empty<double>();
    public double Accuracy { get; init; }
    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1Score { get; init; }
    public double BalancedAccuracy { get; init; }
    public double LogLoss { get; init; }
    public int TrainSamples { get; init; }
    public int TestSamples { get; init; }
    public int PositiveTrainSamples { get; init; }
    public int NegativeTrainSamples { get; init; }
    public DateTime TrainedAtUtc { get; init; }
}
