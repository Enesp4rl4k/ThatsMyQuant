using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    // --- Health Status ---
    [ObservableProperty] private string _listenerHealth = "Unknown";
    [ObservableProperty] private string _marketDataHealth = "Unknown";
    [ObservableProperty] private string _aiServiceHealth = "Unknown";

    // --- Kill Switch & Risk Guard ---
    [ObservableProperty] private bool _isKillSwitchArmed = false;
    [ObservableProperty] private string _riskGuardStatus = "Armed: No";

    // --- Network / Latency ---
    [ObservableProperty] private string _apiPing = "-- ms";
    [ObservableProperty] private Brush _apiPingColor = GrayBrush;
    [ObservableProperty] private string _serverLatency = "-- ms";
    [ObservableProperty] private Brush _serverLatencyColor = GrayBrush;

    // --- Execution Queue Stats ---
    [ObservableProperty] private int _queuedCount = 0;
    [ObservableProperty] private int _executedCount = 0;
    [ObservableProperty] private int _failedCount = 0;
    [ObservableProperty] private int _deadLetterCount = 0;
    [ObservableProperty] private string _queueHealth = "Optimal";

    public void SetMarketHealth(string status)
    {
        ListenerHealth = status == "Online" ? "Online" : "Warning";
        MarketDataHealth = status;
    }

    public void RefreshRiskPanel(bool isKillSwitchArmed)
    {
        IsKillSwitchArmed = isKillSwitchArmed;
        RiskGuardStatus = isKillSwitchArmed ? "Armed: Yes" : "Armed: No";
    }

    public void UpdateQueueStats(int queued, int executed, int failed, int deadLetter)
    {
        QueuedCount = queued;
        ExecutedCount = executed;
        FailedCount = failed;
        DeadLetterCount = deadLetter;

        if (deadLetter > 0) QueueHealth = "Critical (Dead-Letter)";
        else if (queued > 5) QueueHealth = "Congested";
        else QueueHealth = "Optimal";
    }
}
