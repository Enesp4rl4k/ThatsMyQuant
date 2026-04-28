using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// Order-book depth heatmap. Color intensity ∝ resting size at each price level.
/// Side bands: bids in green-blue gradient (left), asks in red-orange gradient
/// (right). The mid-line (best bid / best ask) is highlighted so liquidity walls
/// and lopsided sides become obvious at a glance.
///
/// Data source: IExchangeService.TryGetLatestOrderBook polled every PollMs ms
/// (default 1s). When the live service is absent, a synthetic book is rendered
/// so the visualization is always testable.
/// </summary>
public partial class OrderBookHeatmapViewModel : ObservableObject, IDisposable
{
    private readonly IExchangeService? _exchange;
    private readonly ILogger<OrderBookHeatmapViewModel>? _logger;
    private DispatcherTimer? _timer;

    [ObservableProperty] private string _symbol = "BTC-USDT";
    [ObservableProperty] private int _pollMs = 1000;
    [ObservableProperty] private int _depthLevels = 30;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string _statusMessage = "Click Start to stream order-book updates.";

    [ObservableProperty] private ObservableCollection<DepthCell> _bidCells = new();
    [ObservableProperty] private ObservableCollection<DepthCell> _askCells = new();
    [ObservableProperty] private ObservableCollection<PriceTick> _priceTicks = new();
    [ObservableProperty] private string _bestBidLabel = "—";
    [ObservableProperty] private string _bestAskLabel = "—";
    [ObservableProperty] private string _spreadLabel = "—";
    [ObservableProperty] private string _imbalanceLabel = "—";
    [ObservableProperty] private Brush _imbalanceColor = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E));

    [ObservableProperty] private double _canvasWidth = 1080;
    [ObservableProperty] private double _canvasHeight = 540;
    private double CenterX => CanvasWidth / 2.0;

    public OrderBookHeatmapViewModel(
        IExchangeService? exchange = null,
        ILogger<OrderBookHeatmapViewModel>? logger = null)
    {
        _exchange = exchange;
        _logger = logger;
        // Start with a synthetic snapshot so the canvas is non-empty on first paint.
        Render(BuildSyntheticBook());
    }

    [RelayCommand]
    private void Start()
    {
        if (IsStreaming) return;
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(200, PollMs)) };
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, PollMs));
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        IsStreaming = true;
        StatusMessage = $"Streaming {Symbol} every {PollMs}ms.";
    }

    [RelayCommand]
    private void Stop()
    {
        _timer?.Stop();
        IsStreaming = false;
        StatusMessage = "Stopped.";
    }

    [RelayCommand]
    private void RefreshOnce() => OnTick(null, EventArgs.Empty!);

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            OrderBookSnapshot book;
            if (_exchange is not null && _exchange.TryGetLatestOrderBook(Symbol, out book) && book.Bids is not null)
            {
                Render(book);
                StatusMessage = $"Live · {Symbol} · {book.Bids.Length}b/{book.Asks.Length}a · ts {book.TimestampMs}";
            }
            else
            {
                Render(BuildSyntheticBook());
                StatusMessage = $"Synthetic feed (live order book unavailable for {Symbol}).";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Order book tick failed");
            StatusMessage = $"Tick error: {ex.Message}";
        }
    }

    private void Render(OrderBookSnapshot book)
    {
        var bids = book.Bids ?? Array.Empty<OrderBookLevel>();
        var asks = book.Asks ?? Array.Empty<OrderBookLevel>();
        if (bids.Length == 0 || asks.Length == 0) return;

        // Sort bids descending (highest first), asks ascending.
        var topBids = bids.OrderByDescending(l => l.Price).Take(DepthLevels).ToArray();
        var topAsks = asks.OrderBy(l => l.Price).Take(DepthLevels).ToArray();

        decimal maxQty = 0;
        foreach (var l in topBids) if (l.Quantity > maxQty) maxQty = l.Quantity;
        foreach (var l in topAsks) if (l.Quantity > maxQty) maxQty = l.Quantity;
        if (maxQty == 0m) maxQty = 1m;

        double rowH = (CanvasHeight - 20) / Math.Max(1, DepthLevels);
        double sideW = CenterX - 20;

        var bidCells = new ObservableCollection<DepthCell>();
        var askCells = new ObservableCollection<DepthCell>();
        var ticks = new ObservableCollection<PriceTick>();

        // Bids stack downward from center; asks stack upward from center.
        // Largest size = brightest. Encode magnitude in a Yellow→Orange→Red ramp for emphasis.
        for (int i = 0; i < topBids.Length; i++)
        {
            var lvl = topBids[i];
            double t = (double)(lvl.Quantity / maxQty);
            double barW = sideW * t;
            double y = 10 + i * rowH;
            bidCells.Add(new DepthCell
            {
                X = CenterX - barW,
                Y = y,
                Width = barW,
                Height = Math.Max(1, rowH - 1),
                Fill = new SolidColorBrush(BidColor(t)),
                PriceLabel = lvl.Price.ToString("N2"),
                QtyLabel = lvl.Quantity.ToString("N4"),
                LabelX = 4,
                LabelY = y + Math.Max(1, rowH - 1) / 2.0 - 7
            });
            if (i % 5 == 0)
                ticks.Add(new PriceTick { X = CenterX - sideW - 6, Y = y + rowH / 2.0 - 6, Text = lvl.Price.ToString("N2"), Brush = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)) });
        }
        for (int i = 0; i < topAsks.Length; i++)
        {
            var lvl = topAsks[i];
            double t = (double)(lvl.Quantity / maxQty);
            double barW = sideW * t;
            double y = 10 + i * rowH;
            askCells.Add(new DepthCell
            {
                X = CenterX,
                Y = y,
                Width = barW,
                Height = Math.Max(1, rowH - 1),
                Fill = new SolidColorBrush(AskColor(t)),
                PriceLabel = lvl.Price.ToString("N2"),
                QtyLabel = lvl.Quantity.ToString("N4"),
                LabelX = CenterX + sideW - 80,
                LabelY = y + Math.Max(1, rowH - 1) / 2.0 - 7
            });
        }

        BidCells = bidCells;
        AskCells = askCells;
        PriceTicks = ticks;

        decimal bestBid = topBids[0].Price;
        decimal bestAsk = topAsks[0].Price;
        decimal spread = bestAsk - bestBid;
        decimal mid = (bestBid + bestAsk) / 2m;
        decimal totalBid = topBids.Sum(b => b.Quantity);
        decimal totalAsk = topAsks.Sum(b => b.Quantity);
        decimal denom = totalBid + totalAsk;
        double imb = denom > 0 ? (double)((totalBid - totalAsk) / denom) : 0;

        BestBidLabel = $"{bestBid:N2}";
        BestAskLabel = $"{bestAsk:N2}";
        SpreadLabel = mid > 0 ? $"{spread:N4}  ·  {(spread / mid):P3} bp" : $"{spread:N4}";
        ImbalanceLabel = $"OFI {imb:+0.000;-0.000;0.000} (bid {totalBid:N0} vs ask {totalAsk:N0})";
        ImbalanceColor = imb > 0.10
            ? new SolidColorBrush(Color.FromRgb(0x37, 0xA9, 0x5B))
            : imb < -0.10
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A))
                : new SolidColorBrush(Color.FromRgb(0xF0, 0x88, 0x3E));
    }

    /// <summary>Bid color ramp: deep blue (cold) → cyan → bright yellow (hot wall).</summary>
    private static Color BidColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        // 0 → (0x14,0x2A,0x6E)  midpoint → (0x1F,0x88,0xC4)  1 → (0xF0,0xE2,0x3D)
        if (t < 0.5)
        {
            double k = t / 0.5;
            byte r = (byte)(0x14 + (0x1F - 0x14) * k);
            byte g = (byte)(0x2A + (0x88 - 0x2A) * k);
            byte b = (byte)(0x6E + (0xC4 - 0x6E) * k);
            return Color.FromArgb(0xCC, r, g, b);
        }
        else
        {
            double k = (t - 0.5) / 0.5;
            byte r = (byte)(0x1F + (0xF0 - 0x1F) * k);
            byte g = (byte)(0x88 + (0xE2 - 0x88) * k);
            byte b = (byte)(0xC4 + (0x3D - 0xC4) * k);
            return Color.FromArgb(0xCC, r, g, b);
        }
    }

    /// <summary>Ask color ramp: deep red (cold) → orange → bright yellow (hot wall).</summary>
    private static Color AskColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t < 0.5)
        {
            double k = t / 0.5;
            byte r = (byte)(0x6E + (0xC4 - 0x6E) * k);
            byte g = (byte)(0x14 + (0x35 - 0x14) * k);
            byte b = (byte)(0x14 + (0x1F - 0x14) * k);
            return Color.FromArgb(0xCC, r, g, b);
        }
        else
        {
            double k = (t - 0.5) / 0.5;
            byte r = (byte)(0xC4 + (0xF0 - 0xC4) * k);
            byte g = (byte)(0x35 + (0xE2 - 0x35) * k);
            byte b = (byte)(0x1F + (0x3D - 0x1F) * k);
            return Color.FromArgb(0xCC, r, g, b);
        }
    }

    private OrderBookSnapshot BuildSyntheticBook()
    {
        var rng = new Random();
        decimal mid = 50_000m + (decimal)(rng.NextDouble() - 0.5) * 200m;
        var bids = new OrderBookLevel[DepthLevels];
        var asks = new OrderBookLevel[DepthLevels];
        for (int i = 0; i < DepthLevels; i++)
        {
            // Random size with occasional "wall" spikes.
            decimal baseQty = 0.5m + (decimal)rng.NextDouble() * 5m;
            if (rng.NextDouble() < 0.10) baseQty *= 6m;
            bids[i] = new OrderBookLevel(mid - (i + 1) * 5m, baseQty);
            decimal askQty = 0.5m + (decimal)rng.NextDouble() * 5m;
            if (rng.NextDouble() < 0.10) askQty *= 6m;
            asks[i] = new OrderBookLevel(mid + (i + 1) * 5m, askQty);
        }
        return new OrderBookSnapshot(Symbol, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), bids, asks);
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }
}

public partial class DepthCell : ObservableObject
{
    [ObservableProperty] private double _x, _y, _width, _height;
    [ObservableProperty] private Brush _fill = new SolidColorBrush(Colors.SlateGray);
    [ObservableProperty] private string _priceLabel = "";
    [ObservableProperty] private string _qtyLabel = "";
    [ObservableProperty] private double _labelX, _labelY;
}

public partial class PriceTick : ObservableObject
{
    [ObservableProperty] private double _x, _y;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private Brush _brush = new SolidColorBrush(Colors.Gray);
}
