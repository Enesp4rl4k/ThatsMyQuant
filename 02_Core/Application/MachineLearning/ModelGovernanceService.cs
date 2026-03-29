using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using System.Linq;

namespace ThatsMyQuant.Application.MachineLearning;

public sealed class ModelGovernanceService
{
    public IReadOnlyList<ModelPerformanceSnapshot> Analyze(IEnumerable<Signal> signals, IEnumerable<Trade> trades)
    {
        var signalRows = signals
            .Where(signal => !string.IsNullOrWhiteSpace(GetModelVersion(signal)))
            .ToList();
        var tradeRows = trades
            .Where(trade => !string.IsNullOrWhiteSpace(trade.MlModelVersion))
            .ToList();

        return signalRows
            .Select(GetModelVersion)
            .Concat(tradeRows.Select(trade => trade.MlModelVersion))
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(version => BuildSnapshot(version!, signalRows, tradeRows))
            .OrderByDescending(snapshot => snapshot.LastSeenUtc)
            .ToList();
    }

    public RetrainingTriggerResult EvaluateRetrainingNeed(IEnumerable<Signal> signals, IEnumerable<Trade> trades, string? activeModelVersion = null)
    {
        var snapshots = Analyze(signals, trades);
        if (snapshots.Count == 0)
        {
            return new RetrainingTriggerResult(false, string.Empty, "No model-version history is available yet.");
        }

        var active = !string.IsNullOrWhiteSpace(activeModelVersion)
            ? snapshots.FirstOrDefault(snapshot => string.Equals(snapshot.ModelVersion, activeModelVersion, StringComparison.OrdinalIgnoreCase))
            : snapshots.First();

        active ??= snapshots.First();
        var previous = snapshots
            .Where(snapshot => !string.Equals(snapshot.ModelVersion, active.ModelVersion, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.LastSeenUtc)
            .FirstOrDefault();

        var reasons = new List<string>();

        if (active.SignalCount >= 8 && active.DriftRate >= 35 && active.SizeTrimRate >= 20)
        {
            reasons.Add($"sustained elevated drift ({active.DriftRate:F1}% drift, {active.SizeTrimRate:F1}% size-trim)");
        }

        if (active.SignalCount >= 8 && active.VetoCount >= 3 && active.VetoRate >= 15)
        {
            reasons.Add($"repeated live vetoes ({active.VetoCount} vetoes, {active.VetoRate:F1}% veto rate)");
        }

        if (previous is not null &&
            active.ClosedTrades >= 4 &&
            previous.ClosedTrades >= 4 &&
            active.ExpectedValue < previous.ExpectedValue &&
            (active.ExpectedValue <= 0m || active.WinRate + 10 < previous.WinRate))
        {
            reasons.Add(
                $"live edge decay after model change ({previous.ModelVersion} EV {previous.ExpectedValue:F2}/WR {previous.WinRate:F1}% -> " +
                $"{active.ModelVersion} EV {active.ExpectedValue:F2}/WR {active.WinRate:F1}%)");
        }

        if (reasons.Count == 0)
        {
            return new RetrainingTriggerResult(
                false,
                active.ModelVersion,
                $"Model {active.ModelVersion} is stable enough for now. Drift {active.DriftRate:F1}%, veto {active.VetoRate:F1}%, EV {active.ExpectedValue:F2}.");
        }

        return new RetrainingTriggerResult(
            true,
            active.ModelVersion,
            $"Retraining is recommended for {active.ModelVersion}: {string.Join("; ", reasons)}.");
    }

    private static ModelPerformanceSnapshot BuildSnapshot(string modelVersion, IReadOnlyCollection<Signal> signals, IReadOnlyCollection<Trade> trades)
    {
        var versionSignals = signals
            .Where(signal => string.Equals(GetModelVersion(signal), modelVersion, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var versionTrades = trades
            .Where(trade => string.Equals(trade.MlModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var closedTrades = versionTrades
            .Where(trade => trade.Status == TradeStatus.Executed && trade.IsClosed)
            .ToList();
        var closedPnls = closedTrades.Select(GetPnl).ToList();
        var winRate = closedPnls.Count == 0 ? 0 : closedPnls.Count(pnl => pnl > 0) * 100.0 / closedPnls.Count;
        var expectedValue = closedPnls.Count == 0 ? 0m : closedPnls.Average();
        var driftSignals = versionSignals.Where(HasDrift).ToList();
        var vetoSignals = versionSignals.Where(IsMlVeto).ToList();
        var sizeTrimSignals = versionSignals.Where(IsSizeTrim).ToList();
        var lastSeen = versionSignals
            .Select(signal => signal.Timestamp)
            .Concat(versionTrades.Select(trade => trade.ExitTime ?? trade.EntryTime))
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        return new ModelPerformanceSnapshot(
            modelVersion,
            versionSignals.Count,
            versionTrades.Count,
            closedTrades.Count,
            winRate,
            expectedValue,
            versionSignals.Count == 0 ? 0 : driftSignals.Count * 100.0 / versionSignals.Count,
            versionSignals.Count == 0 ? 0 : vetoSignals.Count * 100.0 / versionSignals.Count,
            versionSignals.Count == 0 ? 0 : sizeTrimSignals.Count * 100.0 / versionSignals.Count,
            vetoSignals.Count,
            sizeTrimSignals.Count,
            driftSignals.Count,
            lastSeen);
    }

    private static string? GetModelVersion(Signal signal)
        => signal.Metadata.TryGetValue("MlModelVersion", out var version) && !string.IsNullOrWhiteSpace(version)
            ? version
            : null;

    private static bool HasDrift(Signal signal)
        => signal.Metadata.TryGetValue("FeatureDriftDetected", out var detected) && bool.TryParse(detected, out var parsed) && parsed
           || signal.Metadata.ContainsKey("FeatureDriftImpactLabel");

    private static bool IsMlVeto(Signal signal)
        => signal.ReasonFlags.HasFlag(SignalReasonFlags.MachineLearningVeto) ||
           (signal.Metadata.TryGetValue("FeatureDriftCritical", out var critical) && bool.TryParse(critical, out var parsed) && parsed);

    private static bool IsSizeTrim(Signal signal)
        => HasDrift(signal) && !IsMlVeto(signal);

    private static decimal GetPnl(Trade trade)
        => (trade.ExitPrice - trade.EntryPrice) * trade.Quantity * (trade.Side == TradeSide.Sell ? -1 : 1);
}

public sealed record ModelPerformanceSnapshot(
    string ModelVersion,
    int SignalCount,
    int TradeCount,
    int ClosedTrades,
    double WinRate,
    decimal ExpectedValue,
    double DriftRate,
    double VetoRate,
    double SizeTrimRate,
    int VetoCount,
    int SizeTrimCount,
    int DriftCount,
    DateTime LastSeenUtc);

public sealed record RetrainingTriggerResult(
    bool TriggerRetrain,
    string ModelVersion,
    string Summary);
