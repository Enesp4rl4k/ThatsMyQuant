using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class AiAnalystViewModel : ObservableObject
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    [ObservableProperty] private string _aiSummary = "Waiting for analysis...";
    [ObservableProperty] private string _aiSentimentMode = "N/A";
    [ObservableProperty] private Brush _aiSentimentColor = GrayBrush;
    [ObservableProperty] private string _aiTradeRecommendation = "Awaiting recommendation";
    [ObservableProperty] private string _aiRiskSummary = "No active AI risk summary.";
    [ObservableProperty] private int _executionReadiness = 0;
    [ObservableProperty] private string _traderSafetyStatus = "Review pending";
    [ObservableProperty] private Brush _traderSafetyColor = GrayBrush;
    [ObservableProperty] private string _tradeReadiness = "Review pending";
    [ObservableProperty] private Brush _tradeReadinessColor = GrayBrush;
    [ObservableProperty] private string _tradeReadinessReason = "Waiting for a fresh market scan.";
    [ObservableProperty] private string _signalConfidenceLabel = "Signal confidence: --";
    [ObservableProperty] private string _executionQualityLabel = "Execution quality: --";
    [ObservableProperty] private string _whyThisTrade = "No active setup is ready for explanation yet.";
    [ObservableProperty] private string _sessionSummary = "Session summary will appear after the next analysis cycle.";
    [ObservableProperty] private string _visualReasoningSummary = "Visual reasoning overlays will appear after analysis.";
    [ObservableProperty] private string _executionPlan = "No active execution plan.";

    // Tier 5 - Meta-Learning
    [ObservableProperty] private string _marketRegime = "Unknown";
    [ObservableProperty] private double _rLReward = 0;
    [ObservableProperty] private string _onlineLearningWeights = "Computing...";
}
