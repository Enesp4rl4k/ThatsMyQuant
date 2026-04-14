import sys
import re

file_path = r'c:\Users\parlak\Downloads\ThatsMyQuant\04_UI\ThatsMyQuant.Dashboard\ViewModels\MainViewModel.cs'

with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Pattern matching the current corrupted state
# 3841:         AddSystemLog("INFRA: Verify Connection currently checks local readiness only.");
# 3842:     }
# 3843:                 CsvEscape(entry.Reasoning)));
# 3844:         }

pattern = r'AddSystemLog\("INFRA: Verify Connection currently checks local readiness only\."\);\s+}\s+CsvEscape\(entry\.Reasoning\)\)\);\s+}\s+'

replacement = r'''AddSystemLog("INFRA: Verify Connection currently checks local readiness only.");
    }

    [RelayCommand]
    private void CalibrateAi()
    {
        AddSystemLog("AI: Intelligence core recalibration started...");
        AddSystemLog($"AI: Tuned FVG Sensitivity to {Studio.FvgSensitivity:F0}%.");
        AddSystemLog($"AI: Threshold synchronized at {Studio.AiConfidenceThreshold:F0}%.");
        AddSystemLog("AI: Calibration successful. Model ready for next signal.");
    }

    private void AddSystemLog(string message)
    {
        InvokeOnUi(() =>
        {
            SystemLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            if (SystemLogs.Count > 100) SystemLogs.RemoveAt(100);
        });
    }

    [RelayCommand]
    private Task ReviewTrade(TradeJournalEntry? trade)
    {
        if (trade == null)
        {
            SelectedTradeReviewTitle = "AI Trade Review";
            SelectedTradeReview = "Choose a closed trade to generate an AI coaching review.";
            return Task.CompletedTask;
        }

        trade = ResolveStoredTruthJournalEntry(trade);

        var confidenceTone = trade.Confidence >= 75
            ? "High-confidence setup"
            : trade.Confidence >= 55
                ? "Moderate-confidence setup"
                : "Low-confidence setup";
        var timingHint = trade.Confidence >= 75
            ? "Execution likely had enough confirmation, so focus on improving exits and scaling."
            : "Wait for stronger candle close confirmation before committing full size.";
        var outcomeHint = trade.Status.Contains("Approved", StringComparison.OrdinalIgnoreCase)
            ? "The setup qualified, but review whether the liquidity sweep or displacement had fully printed first."
            : "The journal status suggests the setup stayed in review, so respect patience over forcing an entry.";
        var driftContext = string.IsNullOrWhiteSpace(trade.MlDriftSummary)
            ? string.Empty
            : $" ML drift context: {trade.MlDriftSummary}.";

        SelectedTradeReviewTitle = $"{trade.Symbol} {trade.Side} review";
        SelectedTradeReview =
            $"{confidenceTone}. {outcomeHint} {timingHint} " +
            $"Audit trail: {BuildAuditTrail(\"; \", trade.EdgeSummary, trade.ExitSummary, trade.MlDriftImpact, trade.ExactBucketImpact, trade.ConcordanceImpact, trade.StageExplanation, trade.AllocatorImpact, trade.VenueImpact)}. " +
            $"{(string.IsNullOrWhiteSpace(trade.MlModelVersion) ? string.Empty : $"Model: {trade.MlModelVersion}. ")}Context: {trade.Reasoning}.{driftContext} Logged at {trade.Time:dd MMM HH:mm}.";
        return Task.CompletedTask;
    }

    private string BuildLedgerExportCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Time,Symbol,Mode,ModelVersion,Side,PromotionStage,StageExplanation,Edge,Exit,MlDrift,Concordance,Venue,Allocator,PnL,Reasoning");

        foreach (var journalEntry in TradeJournal)
        {
            var entry = ResolveStoredTruthJournalEntry(journalEntry);
            builder.AppendLine(string.Join(",",
                CsvEscape(entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                CsvEscape(entry.Symbol),
                CsvEscape(entry.ExecutionMode),
                CsvEscape(entry.MlModelVersion),
                CsvEscape(entry.Side),
                CsvEscape(entry.PromotionStage),
                CsvEscape(entry.StageExplanation),
                CsvEscape(entry.EdgeSummary),
                CsvEscape(entry.ExitSummary),
                CsvEscape(entry.MlDriftImpact),
                CsvEscape(entry.ConcordanceImpact),
                CsvEscape(entry.VenueImpact),
                CsvEscape(entry.AllocatorImpact),
                CsvEscape(entry.PnL.ToString("F2", CultureInfo.InvariantCulture)),
                CsvEscape(entry.Reasoning)));
        }

        return builder.ToString();
    }
'''

if re.search(pattern, content):
    new_content = re.sub(pattern, replacement, content)
    with open(file_path, 'w', encoding='utf-8') as f:
        f.write(new_content)
    print("Successfully patched.")
else:
    print("Pattern not found.")
