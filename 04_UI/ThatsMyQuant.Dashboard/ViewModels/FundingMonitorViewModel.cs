using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Interfaces;
using Microsoft.Extensions.Logging;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// Live funding-rate monitor. For each watched perpetual symbol, shows the current
/// funding rate, color-coded against the validated entry thresholds (±1bp / ±2bp).
/// Refresh is manual (button) — data comes from OKX public funding-rate endpoint
/// through IFundingRateProvider cache.
/// </summary>
public partial class FundingMonitorViewModel : ObservableObject
{
    private readonly IFundingRateProvider? _fundingProvider;
    private readonly ILogger<FundingMonitorViewModel>? _logger;

    [ObservableProperty] private ObservableCollection<FundingRateRow> _rows = new();
    [ObservableProperty] private string _statusMessage = "Click Refresh to pull latest funding rates from OKX.";
    [ObservableProperty] private Brush _statusColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private DateTime _lastRefreshUtc;
    [ObservableProperty] private bool _autoRefreshEnabled;

    private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;

    // Default symbol list — validated-alpha coins + majors for context.
    private static readonly string[] DefaultSymbols =
    {
        "BTC-USDT", "ETH-USDT", "SOL-USDT", "AVAX-USDT", "LINK-USDT",
        "LTC-USDT", "DOGE-USDT", "XRP-USDT", "ADA-USDT", "AAVE-USDT",
        "ATOM-USDT", "DOT-USDT", "NEAR-USDT", "APT-USDT", "SUI-USDT",
        "ARB-USDT", "OP-USDT", "INJ-USDT", "SHIB-USDT", "FIL-USDT"
    };

    public FundingMonitorViewModel(
        IFundingRateProvider? fundingProvider = null,
        ILogger<FundingMonitorViewModel>? logger = null)
    {
        _fundingProvider = fundingProvider;
        _logger = logger;

        foreach (var s in DefaultSymbols)
            Rows.Add(new FundingRateRow { Symbol = s });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_fundingProvider is null)
        {
            StatusMessage = "IFundingRateProvider not wired in DI.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
            return;
        }
        if (IsRefreshing) return;

        IsRefreshing = true;
        StatusMessage = "Fetching funding rates from OKX...";
        StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));

        try
        {
            // Prefetch all symbols to warm cache.
            await _fundingProvider.PrefetchAsync(Rows.Select(r => r.Symbol), DateTime.UtcNow.AddDays(-90));

            var now = DateTime.UtcNow;
            var sparkFrom = now.AddDays(-10);  // ~30 funding observations (3/day × 10d).
            foreach (var row in Rows)
            {
                var rate = _fundingProvider.GetRate(row.Symbol, now);
                row.CurrentRate = rate;
                row.RateBps = (double)(rate * 10_000m);   // fraction → bps
                row.LastUpdatedUtc = now;

                // Signal classification against validated thresholds.
                (row.Signal, row.SignalColor) = rate switch
                {
                    _ when rate >= 0.0003m => ("SHORT @ 3bp+ (crowded long)", new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A))),
                    _ when rate <= -0.0003m => ("LONG @ 3bp- (crowded short)", new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))),
                    _ when rate >= 0.0001m => ("WATCH: SHORT @ 1bp+", new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E))),
                    _ when rate <= -0.0001m => ("WATCH: LONG @ 1bp-", new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E))),
                    _ => ("neutral", new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)))
                };

                // Sparkline — last ~30 points.
                var hist = _fundingProvider.GetHistory(row.Symbol, sparkFrom, now);
                if (hist.Count >= 2)
                {
                    var tail = hist.Count <= 30 ? hist : (IReadOnlyList<(DateTime, decimal)>)hist.TakeLast(30).ToList();
                    double minBps = tail.Min(t => (double)(t.Item2 * 10_000m));
                    double maxBps = tail.Max(t => (double)(t.Item2 * 10_000m));
                    double range = maxBps - minBps;
                    if (range < 0.01) range = 0.01;  // flatline guard

                    double W = row.SparklineWidth;
                    double H = row.SparklineHeight;
                    double pad = 2;
                    var pts = new System.Windows.Media.PointCollection(tail.Count);
                    for (int i = 0; i < tail.Count; i++)
                    {
                        var bps = (double)(tail[i].Item2 * 10_000m);
                        double x = pad + (W - 2 * pad) * i / Math.Max(1, tail.Count - 1);
                        double y = H - pad - ((bps - minBps) / range) * (H - 2 * pad);
                        pts.Add(new System.Windows.Point(x, y));
                    }
                    row.SparklinePoints = pts;
                    row.SparklineMinBps = minBps;
                    row.SparklineMaxBps = maxBps;
                }
                else
                {
                    row.SparklinePoints = new System.Windows.Media.PointCollection();
                }
            }

            LastRefreshUtc = now;
            var signalsHit = Rows.Count(r => Math.Abs(r.RateBps) >= 1.0);
            StatusMessage = $"Refreshed {Rows.Count} symbols at {now:HH:mm:ss} UTC. {signalsHit} signals above 1bp.";
            StatusColor = new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Funding refresh failed");
            StatusMessage = $"Refresh failed: {ex.Message}";
            StatusColor = new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A));
        }
        finally { IsRefreshing = false; }
    }

    partial void OnAutoRefreshEnabledChanged(bool value)
    {
        if (value)
        {
            _autoRefreshTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };
            _autoRefreshTimer.Tick -= OnAutoRefreshTick;
            _autoRefreshTimer.Tick += OnAutoRefreshTick;
            _autoRefreshTimer.Start();
            _ = RefreshAsync();  // fire immediately on enable
        }
        else
        {
            _autoRefreshTimer?.Stop();
        }
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }
}

public partial class FundingRateRow : ObservableObject
{
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private decimal _currentRate;
    [ObservableProperty] private double _rateBps;
    [ObservableProperty] private string _signal = "—";
    [ObservableProperty] private Brush _signalColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));
    [ObservableProperty] private DateTime _lastUpdatedUtc;

    // Sparkline — last 30 funding observations mapped to 120×24 pixel canvas.
    [ObservableProperty] private System.Windows.Media.PointCollection _sparklinePoints = new();
    [ObservableProperty] private double _sparklineWidth = 120;
    [ObservableProperty] private double _sparklineHeight = 24;
    [ObservableProperty] private double _sparklineMinBps;
    [ObservableProperty] private double _sparklineMaxBps;
}
