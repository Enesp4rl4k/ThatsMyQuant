import sys

file_path = r'c:\Users\parlak\Downloads\ThatsMyQuant\04_UI\ThatsMyQuant.Dashboard\ViewModels\MainViewModel.cs'

with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# The broken block we see in the latest view_file (approximate match to avoid line ending issues)
# 3840:         AddSystemLog($"INFRA: {exchangeMode} mode selected.");
# 3841:         AddSystemLog("INFRA: Verify Connection currently checks local readiness only.");
# 3842:     }
# 3843:                 CsvEscape(entry.ExecutionMode),

broken_marker = 'AddSystemLog("INFRA: Verify Connection currently checks local readiness only.");\n    }\n                CsvEscape(entry.ExecutionMode),'

# Corrected block (raw string to avoid escape issues)
corrected_block = r'''AddSystemLog("INFRA: Verify Connection currently checks local readiness only.");
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
            $"Audit trail: {BuildAuditTrail("; ", trade.EdgeSummary, trade.ExitSummary, trade.MlDriftImpact, trade.ExactBucketImpact, trade.ConcordanceImpact, trade.StageExplanation, trade.AllocatorImpact, trade.VenueImpact)}. " +
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
                CsvEscape(entry.ExecutionMode),'''

if broken_marker in content:
    new_content = content.replace(broken_marker, corrected_block)
    # Also check if we missed the leading newline
    if '}\n                CsvEscape' in new_content:
         print("Warning: Partial replacement only.")
    
    with open(file_path, 'w', encoding='utf-8') as f:
        f.write(new_content)
    print("Successfully patched.")
else:
    # Try alternate line endings
    broken_marker_crlf = broken_marker.replace('\n', '\r\n')
    if broken_marker_crlf in content:
        new_content = content.replace(broken_marker_crlf, corrected_block.replace('\n', '\r\n'))
        with open(file_path, 'w', encoding='utf-8', newline='') as f:
            f.write(new_content)
        print("Successfully patched (CRLF).")
    else:
        print("Target block not found. Checking content around 3840...")
        print(content[content.find('INFRA:'):content.find('INFRA:')+500])
