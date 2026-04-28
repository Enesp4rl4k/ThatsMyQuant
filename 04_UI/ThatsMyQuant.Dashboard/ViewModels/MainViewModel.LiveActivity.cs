using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard.ViewModels;

public partial class MainViewModel
{
    private static readonly Brush ActivityBrushOpen = FreezeActivityBrush(0x3F, 0xB9, 0x50);
    private static readonly Brush ActivityBrushClose = FreezeActivityBrush(0x38, 0xBD, 0xF8);
    private static readonly Brush ActivityBrushSignal = FreezeActivityBrush(0xA7, 0x8B, 0xFA);
    private static readonly Brush ActivityBrushReject = FreezeActivityBrush(0xF7, 0x5F, 0x68);
    private static readonly Brush ActivityBrushSystem = FreezeActivityBrush(0x94, 0xA3, 0xB8);

    public ObservableCollection<LiveActivityEntry> LiveActivityFeed { get; } = new();

    [ObservableProperty] private string _liveActivitySummary = "Watching for bot activity…";
    [ObservableProperty] private int _liveActivityTradeOpenCount;
    [ObservableProperty] private int _liveActivityTradeCloseCount;
    [ObservableProperty] private int _liveActivitySignalCount;
    [ObservableProperty] private bool _isLiveActivityVisible = true;

    private readonly HashSet<Guid> _liveActivitySeenTradeIds = new();
    private readonly HashSet<Guid> _liveActivitySeenSignalIds = new();
    private DispatcherTimer? _liveActivityTimer;
    private const int LiveActivityMaxEntries = 60;

    public void StartLiveActivityFeed()
    {
        if (_liveActivityTimer is not null) return;
        _liveActivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _liveActivityTimer.Tick += (_, _) => RefreshLiveActivityFromCaches();
        _liveActivityTimer.Start();
        AddSystemActivity("Live activity feed armed — watching trade + signal caches every 3s.");
    }

    public void StopLiveActivityFeed()
    {
        _liveActivityTimer?.Stop();
        _liveActivityTimer = null;
    }

    private void RefreshLiveActivityFromCaches()
    {
        try
        {
            // Detect new closed trades and new open trades by Id.
            foreach (var trade in _tradeCache)
            {
                if (trade.Id == Guid.Empty) continue;
                if (_liveActivitySeenTradeIds.Contains(trade.Id))
                {
                    // Already-seen trade — emit close event if it just transitioned to closed.
                    continue;
                }

                _liveActivitySeenTradeIds.Add(trade.Id);

                // Skip back-fill noise on first poll (only emit events for trades that opened
                // after the dashboard started). Use EntryTime within last hour as heuristic.
                if (trade.EntryTime < DateTime.UtcNow.AddMinutes(-30)) continue;

                var side = trade.Side == TradeSide.Buy ? "LONG" : "SHORT";
                var headline = $"OPENED {side} {trade.Symbol} @ {trade.EntryPrice:F4}";
                var detail = $"qty={trade.Quantity:F4} · SL={trade.StopLoss:F4} · TP={trade.TakeProfit:F4} · strategy={trade.StrategyTag}";
                AddActivity("OPEN", trade.Symbol, headline, detail, ActivityBrushOpen);
                LiveActivityTradeOpenCount++;
            }

            // Detect new signals (queue events) by Id.
            foreach (var signal in _signalCache)
            {
                if (signal.Id == Guid.Empty) continue;
                if (_liveActivitySeenSignalIds.Contains(signal.Id)) continue;
                _liveActivitySeenSignalIds.Add(signal.Id);
                if (signal.Timestamp < DateTime.UtcNow.AddMinutes(-30)) continue;

                var headline = $"SIGNAL {signal.Side} {signal.Symbol} @ {signal.Price:F4} (conf {signal.Confidence:F0})";
                var detail = $"strategy={signal.StrategyTag} · regime={signal.RegimeTag}";
                AddActivity("SIGNAL", signal.Symbol, headline, detail, ActivityBrushSignal);
                LiveActivitySignalCount++;
            }

            UpdateActivitySummary();
        }
        catch
        {
            // Polling errors shouldn't crash the UI.
        }
    }

    private void UpdateActivitySummary()
    {
        var openPositions = PortfolioView.ActivePositions.Count;
        LiveActivitySummary =
            $"📡 Live: {openPositions} open | opens {LiveActivityTradeOpenCount} | closes {LiveActivityTradeCloseCount} | signals {LiveActivitySignalCount}";
    }

    public void AddActivity(string kind, string symbol, string headline, string detail, Brush brush)
    {
        InvokeOnUi(() =>
        {
            LiveActivityFeed.Insert(0, new LiveActivityEntry(DateTime.UtcNow, kind, symbol, headline, detail, brush));
            while (LiveActivityFeed.Count > LiveActivityMaxEntries)
                LiveActivityFeed.RemoveAt(LiveActivityFeed.Count - 1);
        });
    }

    public void AddSystemActivity(string headline, string detail = "")
        => AddActivity("SYS", "—", headline, detail, ActivityBrushSystem);

    public void AddPositionCloseActivity(Trade trade, decimal pnl)
    {
        var headline = $"CLOSED {trade.Symbol} · PnL {pnl:+0.00;-0.00;0.00}";
        var detail = $"exit={trade.ExitPrice:F4} · reason={trade.ExitReason} · fees={trade.TotalFeesPaid:N2}";
        var brush = pnl >= 0m ? ActivityBrushClose : ActivityBrushReject;
        AddActivity("CLOSE", trade.Symbol, headline, detail, brush);
        LiveActivityTradeCloseCount++;
    }

    private static Brush FreezeActivityBrush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        if (br.CanFreeze) br.Freeze();
        return br;
    }
}
