using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class HftTelemetryViewModel : ObservableObject
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    [ObservableProperty] private string _hftInferenceStatus = "Offline";
    [ObservableProperty] private string _zScoreStatus = "N/A";
    [ObservableProperty] private string _hftWorkspaceHeadline = "Execution telemetry is waiting for the next scan.";
    [ObservableProperty] private string _hftLatencySummary = "Latency profile unavailable";
    [ObservableProperty] private string _hftMicrostructureSummary = "Microstructure context unavailable";
    [ObservableProperty] private string _hftExecutionBias = "Standby";
    [ObservableProperty] private Brush _hftExecutionBiasColor = GrayBrush;
    [ObservableProperty] private string _hftRiskSummary = "HFT risk controls are waiting for queue and market data telemetry.";
    [ObservableProperty] private string _hftFillQuality = "Unknown";
    [ObservableProperty] private Brush _hftFillQualityColor = GrayBrush;
    [ObservableProperty] private string _hftEstimatedSlippage = "~-- bps";
    [ObservableProperty] private string _hftSessionPnl = "0";
    [ObservableProperty] private Brush _hftSessionPnlColor = GrayBrush;
    [ObservableProperty] private string _hftSessionWinRate = "WR --";
    [ObservableProperty] private string _analysisTimestamp = "Updated --:--:--";

    // Tier 1 - Foundation
    [ObservableProperty] private double _orderBookRatio = 1.0;
    [ObservableProperty] private double _tickDirectionRatio = 0.5;
    [ObservableProperty] private double _ofiImbalance = 0.5;

    // Tier 2 - Patterns
    [ObservableProperty] private double _vwapSigma = 0;
    [ObservableProperty] private string _ichimokuCloudStatus = "Neutral";
    [ObservableProperty] private double _bollingerWidth = 0;
}
