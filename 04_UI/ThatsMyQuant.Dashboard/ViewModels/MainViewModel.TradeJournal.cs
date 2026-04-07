using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private void RefreshTradeJournalFromTrades(IReadOnlyList<Trade> trades)
    {
        TradeJournal.Clear();
        PortfolioView.TradeJournal.Clear();

        var journalRows = trades
            .Where(trade =>
                trade.Status == TradeStatus.Executed ||
                trade.Status == TradeStatus.Failed ||
                trade.IsPositionOpen ||
                trade.LifecycleStatus == TradeLifecycleStatus.Submitted ||
                trade.LifecycleStatus == TradeLifecycleStatus.Accepted)
            .OrderByDescending(trade => trade.ExitTime ?? trade.EntryTime)
            .Take(120)
            .Select(BuildTradeJournalEntryFromTrade)
            .ToList();

        foreach (var row in journalRows)
        {
            TradeJournal.Add(row);
            PortfolioView.TradeJournal.Add(row);
        }

        RefreshOperatorAuditAlerts();
    }

    private TradeJournalEntry BuildTradeJournalEntryFromTrade(Trade trade)
    {
        var pnl = CalculateTradePnL(trade);
        var storedVenueImpact = string.IsNullOrWhiteSpace(trade.VenueImpactLabel)
            ? ResolveVenueImpactFromTrade(trade)
            : trade.VenueImpactLabel;
        var exactBucketPressure = ResolveStoredExactBucketPressure(trade);
        var allocatorImpact = ResolveAllocatorImpactFromTrade(trade);
        var concordance = ResolveStoredConcordance(trade, exactBucketPressure, storedVenueImpact);
        var mlDrift = ResolveStoredMlDrift(trade);
        var stageExplanation = BuildTradeJournalStageExplanation(trade);

        return new TradeJournalEntry
        {
            Time = trade.ExitTime ?? trade.EntryTime,
            Symbol = trade.Symbol,
            Side = trade.Side.ToString(),
            Price = trade.EntryPrice,
            Confidence = trade.EdgeScore > 0 ? (int)Math.Clamp(decimal.ToDouble(trade.EdgeScore), 0, 100) : 0,
            Status = ResolveTradeJournalStatus(trade),
            LifecycleStatus = ResolveLifecycleLabel(trade),
            ExecutionMode = NormalizeExecutionMode(trade.ExecutionMode),
            MlModelVersion = trade.MlModelVersion,
            PromotionStage = string.IsNullOrWhiteSpace(trade.PromotionStage) ? "Backtest" : trade.PromotionStage,
            StageExplanation = stageExplanation,
            ExactBucketImpact = exactBucketPressure.Impact,
            ExactBucketSummary = exactBucketPressure.Summary,
            EdgeSummary = BuildTradeJournalEdgeSummary(trade),
            ExitSummary = BuildTradeJournalExitSummary(trade),
            ExitReason = string.IsNullOrWhiteSpace(trade.ExitReason) ? ResolveLifecycleLabel(trade) : trade.ExitReason,
            MlDriftImpact = mlDrift.Impact,
            MlDriftSummary = mlDrift.Summary,
            VenueImpact = storedVenueImpact,
            AllocatorImpact = allocatorImpact,
            ConcordanceImpact = concordance.Impact,
            ConcordanceSummary = concordance.Summary,
            MlDriftImpactColor = ResolveMlDriftImpactColorFromTrade(trade),
            AllocatorImpactColor = ResolveAllocatorImpactColorFromTrade(trade),
            VenueImpactColor = ResolveVenueImpactColorFromTrade(trade),
            ExecutionModeColor = ResolveExecutionModeColor(trade.ExecutionMode),
            SideColor = pnl >= 0 ? GreenBrush : RedBrush,
            PnL = pnl,
            Reasoning = BuildTradeJournalReasoning(
                trade,
                allocatorImpact,
                stageExplanation,
                storedVenueImpact,
                exactBucketPressure.Impact,
                concordance.Impact,
                mlDrift.Impact),
            OriginalTrade = trade
        };
    }

    async partial void OnSelectedTradeJournalEntryChanged(TradeJournalEntry? value)
    {
        if (value?.OriginalTrade == null)
        {
            SelectedTradeReview = "Choose a closed trade to generate an AI coaching review.";
            return;
        }

        SelectedTradeReview = "Generating AI Trade Review... [Institutional Reasoner Active]";
        AddSystemLog($"AI: Analyzing trade performance for {value.Symbol}...");

        try
        {
            var review = await _aiAnalyst.ReviewTradeAsync(value.OriginalTrade);
            SelectedTradeReview = review;
            AddSystemLog($"AI: Post-trade coaching completed for {value.Symbol}.");
        }
        catch (Exception ex)
        {
            SelectedTradeReview = "Failed to generate AI review. Technical error in DeepSeek-R1 reasoning pipeline.";
            AddSystemLog($"AI ERROR: Trade review failed: {ex.Message}");
        }
    }

    private Trade? ResolvePersistedTradeForJournalEntry(TradeJournalEntry entry)
    {
        return _tradeCache
            .Where(trade => trade.Status == TradeStatus.Executed || trade.Status == TradeStatus.Failed)
            .Where(trade => string.Equals(trade.Symbol, entry.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(trade => string.Equals(trade.Side.ToString(), entry.Side, StringComparison.OrdinalIgnoreCase))
            .OrderBy(trade =>
            {
                var timestamp = trade.ExitTime ?? trade.EntryTime;
                return Math.Abs((timestamp - entry.Time).TotalSeconds);
            })
            .FirstOrDefault(trade => Math.Abs(((trade.ExitTime ?? trade.EntryTime) - entry.Time).TotalMinutes) <= 5);
    }

    private TradeJournalEntry ResolveStoredTruthJournalEntry(TradeJournalEntry entry)
    {
        var persistedTrade = ResolvePersistedTradeForJournalEntry(entry);
        return persistedTrade == null
            ? entry
            : BuildTradeJournalEntryFromTrade(persistedTrade);
    }

    private (string Impact, string Summary) ResolveStoredExactBucketPressure(Trade trade)
    {
        if (!string.IsNullOrWhiteSpace(trade.ExactBucketImpactLabel) ||
            !string.IsNullOrWhiteSpace(trade.ExactBucketSummary))
        {
            return (
                string.IsNullOrWhiteSpace(trade.ExactBucketImpactLabel) ? "Exact bucket pressure unconfirmed" : trade.ExactBucketImpactLabel,
                string.IsNullOrWhiteSpace(trade.ExactBucketSummary) ? "No exact bucket pressure signal has enough stored depth yet." : trade.ExactBucketSummary);
        }

        return ResolveExactBucketPressureFromTrade(trade);
    }

    private (string Impact, string Summary) ResolveStoredConcordance(
        Trade trade,
        (string Impact, string Summary) exactBucketPressure,
        string venueImpact)
    {
        if (!string.IsNullOrWhiteSpace(trade.ConcordanceImpactLabel) ||
            !string.IsNullOrWhiteSpace(trade.ConcordanceSummary))
        {
            return (
                string.IsNullOrWhiteSpace(trade.ConcordanceImpactLabel) ? "Concordance unconfirmed" : trade.ConcordanceImpactLabel,
                string.IsNullOrWhiteSpace(trade.ConcordanceSummary)
                    ? $"Stored concordance summary missing. {exactBucketPressure.Summary} {venueImpact}"
                    : trade.ConcordanceSummary);
        }

        var venueScope = string.IsNullOrWhiteSpace(trade.VenueExecutionScope) ? "default venue scope" : trade.VenueExecutionScope;
        var display = BuildConcordanceDisplay(
            exactBucketPressure.Impact,
            exactBucketPressure.Summary,
            trade.VenueExecutionBias,
            $"{venueImpact}. Scope: {venueScope}.");
        return (display.Impact, display.Summary);
    }

    private static (string Impact, string Summary) ResolveStoredMlDrift(Trade trade)
    {
        if (!trade.FeatureDriftDetected &&
            string.IsNullOrWhiteSpace(trade.FeatureDriftImpactLabel) &&
            string.IsNullOrWhiteSpace(trade.FeatureDriftSummary))
        {
            return (string.Empty, string.Empty);
        }

        var impact = string.IsNullOrWhiteSpace(trade.FeatureDriftImpactLabel)
            ? trade.FeatureDriftCritical
                ? "ML drift vetoed setup"
                : "ML drift trimmed size"
            : trade.FeatureDriftImpactLabel;

        var summary = string.IsNullOrWhiteSpace(trade.FeatureDriftSummary)
            ? $"Stored ML drift flagged {trade.DriftedFeatureCount} features with max z-score {trade.FeatureDriftMaxZScore.ToString("F2", CultureInfo.InvariantCulture)}."
            : trade.FeatureDriftSummary;

        return (impact, summary);
    }

    private (string Impact, string Summary) ResolveExactBucketPressureFromTrade(Trade trade)
    {
        var sessionTag = ResolveSessionBucketForTrade(trade);
        var exactBucketTrades = _tradeCache
            .Where(candidate => candidate.Status == TradeStatus.Executed || candidate.Status == TradeStatus.Failed)
            .Where(candidate => string.Equals(candidate.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(candidate.StrategyTag, trade.StrategyTag, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(candidate.RegimeTag, trade.RegimeTag, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(ResolveSessionBucketForTrade(candidate), sessionTag, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactBucketTrades.Count == 0)
        {
            return ("Exact bucket pressure unconfirmed", "No exact bucket pressure signal has enough stored depth yet.");
        }

        var display = BuildExactBucketPressureDisplay(exactBucketTrades);
        return (display.Impact, display.Summary);
    }

    private static string ResolveVenueImpactFromTrade(Trade trade)
        => FormatBiasImpact(
            trade.VenueExecutionBias,
            neutralLabel: "Venue neutral",
            hardLowLabel: "Venue hurt size hard",
            softLowLabel: "Venue capped size",
            highLabel: "Venue helped size");

    private Brush ResolveVenueImpactColorFromTrade(Trade trade)
        => ResolveBiasColor(trade.VenueExecutionBias);

    private Brush ResolveMlDriftImpactColorFromTrade(Trade trade)
    {
        if (!trade.FeatureDriftDetected && string.IsNullOrWhiteSpace(trade.FeatureDriftImpactLabel))
        {
            return GrayBrush;
        }

        return trade.FeatureDriftCritical ||
               trade.FeatureDriftImpactLabel.Contains("veto", StringComparison.OrdinalIgnoreCase)
            ? RedBrush
            : OrangeBrush;
    }

    private static string ResolveAllocatorImpactFromTrade(Trade trade)
        => FormatBiasImpact(
            trade.FinalAllocatorBias,
            neutralLabel: "Allocator neutral",
            hardLowLabel: "Allocator cut size hard",
            softLowLabel: "Allocator trimmed size",
            highLabel: "Allocator boosted size");

    private Brush ResolveAllocatorImpactColorFromTrade(Trade trade)
        => ResolveBiasColor(trade.FinalAllocatorBias);

    private static string FormatBiasImpact(
        decimal bias,
        string neutralLabel,
        string hardLowLabel,
        string softLowLabel,
        string highLabel)
    {
        var biasText = bias.ToString("F2", CultureInfo.InvariantCulture);

        if (bias <= 0m)
        {
            return neutralLabel;
        }

        if (bias <= 0.93m)
        {
            return $"{hardLowLabel} ({biasText}x)";
        }

        if (bias <= 0.98m)
        {
            return $"{softLowLabel} ({biasText}x)";
        }

        if (bias >= 1.02m)
        {
            return $"{highLabel} ({biasText}x)";
        }

        return neutralLabel;
    }

    private Brush ResolveBiasColor(decimal bias)
    {
        if (bias <= 0m)
        {
            return GrayBrush;
        }

        if (bias <= 0.93m)
        {
            return RedBrush;
        }

        if (bias <= 0.98m)
        {
            return OrangeBrush;
        }

        if (bias >= 1.02m)
        {
            return GreenBrush;
        }

        return GrayBrush;
    }

    private static string BuildTradeJournalEdgeSummary(Trade trade)
    {
        if (trade.EdgeScore <= 0m && string.IsNullOrWhiteSpace(trade.EdgeBand))
        {
            return "Edge n/a";
        }

        var edgeScoreText = trade.EdgeScore > 0m
            ? trade.EdgeScore.ToString("F1", CultureInfo.InvariantCulture)
            : "--";
        var edgeBand = string.IsNullOrWhiteSpace(trade.EdgeBand) ? "n/a" : trade.EdgeBand;
        return $"Edge {edgeScoreText} [{edgeBand}]";
    }

    private static string BuildTradeJournalExitSummary(Trade trade)
    {
        var policy = string.IsNullOrWhiteSpace(trade.ExitPolicyTag)
            ? "n/a"
            : trade.ExitPolicyTag;
        var reason = string.IsNullOrWhiteSpace(trade.ExitReason) ? string.Empty : $" | {trade.ExitReason}";
        return $"Exit {policy}{reason}";
    }

    private Brush ResolveExecutionModeColor(string? executionMode)
    {
        return NormalizeExecutionMode(executionMode) switch
        {
            "REAL" => RedBrush,
            "PAPER" => OrangeBrush,
            "SIMULATED" => GrayBrush,
            _ => GrayBrush
        };
    }

    private static string BuildTradeJournalStageExplanation(Trade trade)
    {
        var stage = string.IsNullOrWhiteSpace(trade.PromotionStage) ? "Backtest" : trade.PromotionStage;
        var scope = string.IsNullOrWhiteSpace(trade.FinalAllocatorScope) ? "default sizing scope" : trade.FinalAllocatorScope;
        return $"Stage {stage} via {scope}";
    }

    private static string BuildTradeJournalReasoning(
        Trade trade,
        string allocatorImpact,
        string stageExplanation,
        string venueImpact,
        string exactBucketImpact,
        string concordanceImpact,
        string mlDriftImpact)
    {
        var edgePart = trade.EdgeScore > 0m
            ? $"Edge {trade.EdgeScore:F1} [{(string.IsNullOrWhiteSpace(trade.EdgeBand) ? "n/a" : trade.EdgeBand)}]"
            : "Edge n/a";
        var exitPart = BuildTradeJournalExitSummary(trade);
        var lifecyclePart = $"Lifecycle {ResolveLifecycleLabel(trade)}";

        return BuildAuditTrail(
            " | ",
            edgePart,
            exitPart,
            lifecyclePart,
            mlDriftImpact,
            exactBucketImpact,
            concordanceImpact,
            stageExplanation,
            allocatorImpact,
            venueImpact);
    }

    private static string BuildAuditTrail(string separator, params string[] parts)
    {
        return string.Join(separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildJournalEdgeSummary(TradeDecision decision)
    {
        var edgeBand = decision.Confidence switch
        {
            >= 85 => "Full",
            >= 70 => "Reduced",
            >= 55 => "Probe",
            _ => "Rejected"
        };

        return $"Edge {decision.Confidence:F1} [{edgeBand}]";
    }

    private static string BuildJournalExitSummary(TradeDecision decision)
    {
        return string.IsNullOrWhiteSpace(decision.ExitPolicyTag)
            ? "Exit n/a"
            : $"Exit {decision.ExitPolicyTag}";
    }

    private string BuildJournalStageExplanation(string promotionStage)
    {
        var scope = StrategyThrottleStatus switch
        {
            "Blocked" => "throttle guard",
            "Reduced" => "reduced-size guard",
            _ => "live journal approval"
        };

        return $"Stage {promotionStage} via {scope}";
    }

    private string BuildJournalAllocatorImpact(TradeDecision decision)
    {
        if (StrategyThrottleStatus == "Blocked" || decision.PositionSizePercent <= 0m)
        {
            return "Allocator neutral";
        }

        if (StrategyThrottleStatus == "Reduced" || Studio.BotLaunchGuardStatus is "Reduced" or "Caution")
        {
            return "Allocator trimmed size (0.95x)";
        }

        if (decision.PositionSizePercent >= 0.03m)
        {
            return "Allocator boosted size (1.05x)";
        }

        return "Allocator neutral";
    }

    private AlertAuditContext BuildAlertAuditContext(string symbol, TradeDecision decision, IndicatorSnapshot indicators)
    {
        var promotionStage = ResolveJournalPromotionStage();
        var exactBucketPressure = ResolveExactBucketPressure(decision.StrategyTag, symbol);
        return new AlertAuditContext
        {
            ExecutionMode = GetCurrentExecutionModeLabel(),
            PromotionStage = promotionStage,
            StageExplanation = BuildJournalStageExplanation(promotionStage),
            ExactBucketImpact = exactBucketPressure?.Impact ?? "Exact bucket pressure unconfirmed",
            ExactBucketSummary = exactBucketPressure?.Summary ?? "No exact bucket pressure signal has enough stored depth yet.",
            EdgeSummary = BuildJournalEdgeSummary(decision),
            ExitSummary = BuildJournalExitSummary(decision),
            MlDriftImpact = string.Empty,
            MlDriftSummary = string.Empty,
            VenueImpact = BuildJournalVenueImpact(decision, indicators),
            AllocatorImpact = BuildJournalAllocatorImpact(decision),
            Reasoning = decision.Reasoning
        };
    }

    private void RefreshOperatorAuditAlerts()
    {
        OperatorAuditAlerts.Clear();

        var rows = TradeJournal
            .Take(4)
            .Select(ResolveStoredTruthJournalEntry)
            .Select(BuildOperatorAuditAlertRow)
            .ToList();

        foreach (var row in rows)
        {
            OperatorAuditAlerts.Add(row);
        }

        if (OperatorAuditAlerts.Count == 0)
        {
            OperatorAuditAlerts.Add(new OperatorAuditAlertRow
            {
                Headline = "Audit trail waiting for executions",
                Detail = "Stored operator alerts will appear after trades are rebuilt from persisted execution context.",
                AccentColor = GrayBrush
            });
        }
    }

    private OperatorAuditAlertRow BuildOperatorAuditAlertRow(TradeJournalEntry entry)
    {
        return new OperatorAuditAlertRow
        {
            Headline = $"{entry.ExecutionMode} {entry.Symbol} {entry.Side} | {entry.PromotionStage}",
            Detail = BuildAuditTrail(
                " | ",
                entry.EdgeSummary,
                entry.ExitSummary,
                entry.MlDriftImpact,
                entry.ExactBucketImpact,
                entry.ConcordanceImpact,
                entry.AllocatorImpact,
                entry.VenueImpact),
            AccentColor = ResolveOperatorAuditAlertColor(entry)
        };
    }

    private Brush ResolveOperatorAuditAlertColor(TradeJournalEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.MlDriftImpact))
        {
            return entry.MlDriftImpact.Contains("veto", StringComparison.OrdinalIgnoreCase)
                ? RedBrush
                : OrangeBrush;
        }

        if (!string.IsNullOrWhiteSpace(entry.VenueImpact) &&
            entry.VenueImpact.Contains("capped", StringComparison.OrdinalIgnoreCase))
        {
            return OrangeBrush;
        }

        if (!string.IsNullOrWhiteSpace(entry.AllocatorImpact) &&
            entry.AllocatorImpact.Contains("trimmed", StringComparison.OrdinalIgnoreCase))
        {
            return OrangeBrush;
        }

        if (string.Equals(entry.PromotionStage, "ScaledLive", StringComparison.OrdinalIgnoreCase))
        {
            return GreenBrush;
        }

        if (string.Equals(entry.PromotionStage, "LiveProbe", StringComparison.OrdinalIgnoreCase))
        {
            return BuyColorBrush;
        }

        return GrayBrush;
    }
}
