using System.Security.Cryptography;
using System.Text;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.MachineLearning;

public sealed class ModelExperimentService
{
    public ModelExperimentAssignment AssignVariant(string experimentId, string subjectKey, IReadOnlyList<string> modelVersions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKey);
        ArgumentNullException.ThrowIfNull(modelVersions);

        var normalizedModels = modelVersions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(version => version, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedModels.Length == 0)
        {
            throw new InvalidOperationException("At least one model version is required for experiment assignment.");
        }

        var bucket = ComputeBucket($"{experimentId}:{subjectKey}");
        var index = bucket % normalizedModels.Length;
        return new ModelExperimentAssignment(experimentId, subjectKey, normalizedModels[index], index, bucket);
    }

    public ModelExperimentEvaluation Evaluate(
        string experimentId,
        IEnumerable<Signal> signals,
        IEnumerable<Trade> trades)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);

        var experimentSignals = signals
            .Where(signal => signal.Metadata.TryGetValue("MlExperimentId", out var id) &&
                             string.Equals(id, experimentId, StringComparison.OrdinalIgnoreCase) &&
                             signal.Metadata.TryGetValue("MlModelVersion", out _))
            .ToList();

        var snapshots = experimentSignals
            .GroupBy(signal => signal.Metadata["MlModelVersion"], StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildSnapshot(group.Key, group.ToList(), trades))
            .OrderByDescending(snapshot => snapshot.CompositeScore)
            .ToList();

        var champion = snapshots.FirstOrDefault();
        return new ModelExperimentEvaluation(
            experimentId,
            snapshots,
            champion?.ModelVersion ?? string.Empty,
            champion is null
                ? "No experiment evidence is available yet."
                : $"Champion {champion.ModelVersion} leads with score {champion.CompositeScore:F2}, EV {champion.ExpectedValue:F2}, WR {champion.WinRate:F1}%.");
    }

    private static ModelExperimentSnapshot BuildSnapshot(
        string modelVersion,
        IReadOnlyCollection<Signal> signals,
        IEnumerable<Trade> trades)
    {
        var tradeRows = trades
            .Where(trade => string.Equals(trade.MlModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase))
            .Where(trade => trade.Status == TradeStatus.Executed && trade.IsClosed)
            .ToList();

        var expectedValue = tradeRows.Count == 0 ? 0m : tradeRows.Average(GetPnl);
        var winRate = tradeRows.Count == 0 ? 0d : tradeRows.Count(trade => GetPnl(trade) > 0m) * 100d / tradeRows.Count;
        var averageConfidence = signals.Count == 0 ? 0d : signals.Average(signal => signal.Confidence);
        var approvalRate = signals.Count == 0 ? 0d : signals.Count(signal => signal.Status != TradeStatus.Rejected) * 100d / signals.Count;
        var compositeScore = (double)expectedValue + (winRate * 0.35d) + (averageConfidence * 0.1d) + (approvalRate * 0.05d);

        return new ModelExperimentSnapshot(
            modelVersion,
            signals.Count,
            tradeRows.Count,
            expectedValue,
            winRate,
            averageConfidence,
            approvalRate,
            compositeScore);
    }

    private static decimal GetPnl(Trade trade)
        => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1);

    private static int ComputeBucket(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }
}

public sealed record ModelExperimentAssignment(
    string ExperimentId,
    string SubjectKey,
    string ModelVersion,
    int VariantIndex,
    int Bucket);

public sealed record ModelExperimentEvaluation(
    string ExperimentId,
    IReadOnlyList<ModelExperimentSnapshot> Snapshots,
    string ChampionModelVersion,
    string Summary);

public sealed record ModelExperimentSnapshot(
    string ModelVersion,
    int SignalCount,
    int ClosedTradeCount,
    decimal ExpectedValue,
    double WinRate,
    double AverageConfidence,
    double ApprovalRate,
    double CompositeScore);
