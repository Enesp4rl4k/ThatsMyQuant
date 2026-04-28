using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Dashboard.ViewModels;

public sealed class OrderBookRowViewModel
{
    public decimal Price { get; init; }
    public decimal Quantity { get; init; }
    public decimal Cumulative { get; init; }
    public double DepthRatio { get; init; } // 0..1 for the bar width
    public double DepthPixels => Math.Max(1.0, DepthRatio * 200.0);
    public bool IsBid { get; init; }
    public string PriceLabel => Price switch
    {
        >= 1000m => Price.ToString("N0"),
        >= 10m => Price.ToString("N2"),
        >= 1m => Price.ToString("N3"),
        _ => Price.ToString("N5"),
    };
    public string QuantityLabel => Quantity switch
    {
        >= 10000m => (Quantity / 1000m).ToString("N1") + "K",
        >= 1m => Quantity.ToString("N2"),
        _ => Quantity.ToString("N4"),
    };
    public Brush RowBrush => IsBid
        ? new SolidColorBrush(Color.FromArgb(36, 0, 255, 187))
        : new SolidColorBrush(Color.FromArgb(36, 255, 51, 102));
    public Brush PriceBrush => IsBid
        ? new SolidColorBrush(Color.FromRgb(0, 255, 187))
        : new SolidColorBrush(Color.FromRgb(255, 51, 102));
}

public sealed class TradeTapeEntryViewModel
{
    public DateTime Time { get; init; }
    public decimal Price { get; init; }
    public decimal Quantity { get; init; }
    public bool IsBuyerMaker { get; init; }
    public string TimeLabel => Time.ToString("HH:mm:ss");
    public string PriceLabel => Price switch
    {
        >= 1000m => Price.ToString("N0"),
        >= 10m => Price.ToString("N2"),
        >= 1m => Price.ToString("N3"),
        _ => Price.ToString("N5"),
    };
    public string QuantityLabel => Quantity >= 1m ? Quantity.ToString("N3") : Quantity.ToString("N5");
    public Brush SideBrush => IsBuyerMaker
        ? new SolidColorBrush(Color.FromRgb(255, 51, 102))
        : new SolidColorBrush(Color.FromRgb(0, 255, 187));
    public string SideLabel => IsBuyerMaker ? "SELL" : "BUY";
}

// Real-time WebSocket wiring. OnTickReceived fires on ThreadPool — always dispatch to UI thread.
public partial class MainViewModel
{
    private CancellationTokenSource? _streamingCts;

    public ObservableCollection<OrderBookRowViewModel> TopBids { get; } = new();
    public ObservableCollection<OrderBookRowViewModel> TopAsks { get; } = new();
    public ObservableCollection<TradeTapeEntryViewModel> RecentTrades { get; } = new();
    private const int OrderBookDepth = 10;
    private const int TradeTapeCapacity = 30;

    private async void StartStreamingIfAvailable()
    {
        if (_streamingService is null || _streamingConnected)
        {
            return;
        }

        var symbols = BuildStreamingSymbolUniverse();
        if (symbols.Length == 0)
        {
            return;
        }

        try
        {
            _streamingService.OnTickReceived += HandleStreamingTick;
            _streamingService.OnOrderBookUpdated += HandleOrderBookUpdate;
            _streamingCts = new CancellationTokenSource();
            await _streamingService.ConnectAsync(symbols, _streamingCts.Token);
            foreach (var s in symbols)
            {
                _streamingSubscribed.Add(s);
            }
            _streamingConnected = true;
            AddSystemLog($"Streaming: subscribed to {symbols.Length} symbol(s).");
        }
        catch (Exception ex)
        {
            AddSystemLog($"Streaming start failed: {ex.Message}");
            _streamingService.OnTickReceived -= HandleStreamingTick;
        }
    }

    private string[] BuildStreamingSymbolUniverse()
    {
        // Merge watchlist + currently selected chart symbol.
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in WatchlistItems)
        {
            if (!string.IsNullOrWhiteSpace(item.Symbol))
            {
                set.Add(item.Symbol);
            }
        }
        if (!string.IsNullOrWhiteSpace(MarketOverview.SelectedSymbol))
        {
            set.Add(MarketOverview.SelectedSymbol);
        }
        return set.Take(20).ToArray();
    }

    private void HandleStreamingTick(MarketTick tick)
    {
        // Normalize to the watchlist symbol format (no dash): e.g. "BTC-USDT" -> "BTCUSDT".
        var normalized = tick.Symbol.Replace("-", string.Empty).ToUpperInvariant();
        var price = tick.Price;
        if (price <= 0m)
        {
            return;
        }

        InvokeOnUi(() =>
        {
            // 1. Watchlist update
            var item = WatchlistItems.FirstOrDefault(w =>
                w.Symbol.Replace("-", string.Empty).Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (item is not null)
            {
                var prev = item.Price;
                item.Price = price;
                if (prev > 0m)
                {
                    item.ChangePercent = (double)((price - prev) / prev * 100m);
                }
            }

            // 2. If this is the currently selected chart symbol, update CurrentPrice + trade tape.
            var selectedNormalized = MarketOverview.SelectedSymbol.Replace("-", string.Empty).ToUpperInvariant();
            if (normalized.Equals(selectedNormalized, StringComparison.OrdinalIgnoreCase))
            {
                CurrentPrice = price;

                RecentTrades.Insert(0, new TradeTapeEntryViewModel
                {
                    Time = DateTimeOffset.FromUnixTimeMilliseconds(tick.TimestampMs).LocalDateTime,
                    Price = price,
                    Quantity = tick.Volume,
                    IsBuyerMaker = tick.IsBuyerMaker,
                });
                while (RecentTrades.Count > TradeTapeCapacity)
                {
                    RecentTrades.RemoveAt(RecentTrades.Count - 1);
                }
            }
        });
    }

    private void HandleOrderBookUpdate(OrderBookSnapshot snapshot)
    {
        var normalized = snapshot.Symbol.Replace("-", string.Empty).ToUpperInvariant();
        var selectedNormalized = MarketOverview.SelectedSymbol.Replace("-", string.Empty).ToUpperInvariant();
        if (!normalized.Equals(selectedNormalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Copy the arrays on the stream thread so we don't race with later updates.
        var bids = snapshot.Bids?.Take(OrderBookDepth).ToArray() ?? Array.Empty<OrderBookLevel>();
        var asks = snapshot.Asks?.Take(OrderBookDepth).ToArray() ?? Array.Empty<OrderBookLevel>();
        if (bids.Length == 0 && asks.Length == 0)
        {
            return;
        }

        decimal bidCum = 0m, askCum = 0m;
        var maxQty = Math.Max(
            bids.Length == 0 ? 0m : bids.Max(b => b.Quantity),
            asks.Length == 0 ? 0m : asks.Max(a => a.Quantity));
        if (maxQty <= 0m)
        {
            maxQty = 1m;
        }

        InvokeOnUi(() =>
        {
            TopBids.Clear();
            foreach (var level in bids)
            {
                bidCum += level.Quantity;
                TopBids.Add(new OrderBookRowViewModel
                {
                    Price = level.Price,
                    Quantity = level.Quantity,
                    Cumulative = bidCum,
                    DepthRatio = (double)(level.Quantity / maxQty),
                    IsBid = true,
                });
            }

            TopAsks.Clear();
            // Asks sorted best-ask-first (ascending). Display top-down with best ask at the bottom (closest to mid).
            foreach (var level in asks.Reverse())
            {
                askCum += level.Quantity;
                TopAsks.Add(new OrderBookRowViewModel
                {
                    Price = level.Price,
                    Quantity = level.Quantity,
                    Cumulative = askCum,
                    DepthRatio = (double)(level.Quantity / maxQty),
                    IsBid = false,
                });
            }
        });
    }

    internal async void EnsureSymbolSubscribed(string symbol)
    {
        if (_streamingService is null || !_streamingConnected || string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (_streamingSubscribed.Contains(symbol))
        {
            return;
        }

        try
        {
            await _streamingService.SubscribeAsync(symbol);
            _streamingSubscribed.Add(symbol);
        }
        catch (Exception ex)
        {
            AddSystemLog($"Streaming subscribe failed for {symbol}: {ex.Message}");
        }
    }

    private void StopStreaming()
    {
        if (_streamingService is null)
        {
            return;
        }

        try
        {
            _streamingService.OnTickReceived -= HandleStreamingTick;
            _streamingService.OnOrderBookUpdated -= HandleOrderBookUpdate;
            _streamingCts?.Cancel();
            _ = _streamingService.DisconnectAsync();
        }
        catch
        {
            // best effort shutdown
        }
        finally
        {
            _streamingConnected = false;
            _streamingSubscribed.Clear();
        }
    }
}
