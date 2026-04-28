using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;
using ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

namespace ThatsMyQuant.Dashboard.ViewModels;

/// <summary>
/// Backs the Position Detail popup: candle chart of the open position's
/// symbol with Entry / SL / TP horizontal overlays plus a right-side audit
/// panel sourced from the matching <see cref="Trade"/>/<see cref="Signal"/>.
/// </summary>
public partial class PositionDetailViewModel : ObservableObject
{
    public sealed class Candle
    {
        public double BodyX { get; init; }
        public double BodyY { get; init; }
        public double BodyWidth { get; init; }
        public double BodyHeight { get; init; }
        public double WickX { get; init; }
        public double WickY1 { get; init; }
        public double WickY2 { get; init; }
        public Brush Fill { get; init; } = Brushes.Gray;
    }

    public sealed class OverlayLine
    {
        public PointCollection Points { get; init; } = new();
        public Brush Stroke { get; init; } = Brushes.White;
        public string Label { get; init; } = "";
        public double LabelY { get; init; }
        public double LabelX { get; init; }
        public DoubleCollection? DashArray { get; init; }
    }

    [ObservableProperty] private string _title = "Position Detail";
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _timeframe = "4h";
    [ObservableProperty] private string _side = "";
    [ObservableProperty] private Brush _sideBrush = Brushes.Gray;
    [ObservableProperty] private string _entryPriceText = "—";
    [ObservableProperty] private string _stopLossText = "—";
    [ObservableProperty] private string _takeProfitText = "—";
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _leverageText = "—";
    [ObservableProperty] private string _unrealizedPnlText = "—";
    [ObservableProperty] private Brush _unrealizedPnlBrush = Brushes.Gray;
    [ObservableProperty] private string _riskRewardText = "—";
    [ObservableProperty] private string _strategyTag = "—";
    [ObservableProperty] private string _regimeTag = "—";
    [ObservableProperty] private string _edgeScoreText = "—";
    [ObservableProperty] private string _edgeBand = "—";
    [ObservableProperty] private string _confidenceText = "—";
    [ObservableProperty] private string _reasoningText = "Bot did not persist a reasoning trail for this position.";
    [ObservableProperty] private string _slippageText = "—";
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string _signalPriceText = "—";
    [ObservableProperty] private string _statusLine = "";
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<Candle> Candles { get; } = new();
    public ObservableCollection<OverlayLine> OverlayLines { get; } = new();

    public const double ChartW = 780d;
    public const double ChartH = 420d;

    private readonly IMarketDataService _marketData;
    private readonly PositionViewModel _position;
    private readonly Trade? _trade;
    private readonly Signal? _signal;

    public PositionDetailViewModel(
        PositionViewModel position,
        IMarketDataService marketData,
        Trade? trade = null,
        Signal? signal = null,
        string timeframe = "4h")
    {
        _position = position;
        _marketData = marketData;
        _trade = trade;
        _signal = signal;
        Timeframe = timeframe;
        PopulateHeaderFromPosition();
        PopulateAuditFromTrade();
    }

    private void PopulateHeaderFromPosition()
    {
        Symbol = _position.Symbol;
        Side = _position.Side;
        SideBrush = _position.SideColor;
        Title = $"POSITION · {_position.Symbol} · {_position.Side}";
        EntryPriceText = _position.EntryPrice.ToString("F6");
        StopLossText = _position.StopLoss > 0m ? _position.StopLoss.ToString("F6") : "—";
        TakeProfitText = _position.TakeProfit > 0m ? _position.TakeProfit.ToString("F6") : "—";
        SizeText = _position.Size.ToString("F6");
        LeverageText = _position.Leverage ?? "—";
        UnrealizedPnlText = _position.UnrealisedPnL.ToString("N2");
        UnrealizedPnlBrush = _position.PnLColor;

        if (_position.EntryPrice > 0m && _position.StopLoss > 0m && _position.TakeProfit > 0m)
        {
            var risk = (double)Math.Abs(_position.EntryPrice - _position.StopLoss);
            var reward = (double)Math.Abs(_position.TakeProfit - _position.EntryPrice);
            var rr = risk > 1e-9 ? reward / risk : 0d;
            RiskRewardText = $"{rr:F2} : 1";
        }
        else
        {
            RiskRewardText = "n/a";
        }
    }

    private void PopulateAuditFromTrade()
    {
        if (_trade is not null)
        {
            StrategyTag = string.IsNullOrWhiteSpace(_trade.StrategyTag) ? "—" : _trade.StrategyTag!;
            RegimeTag = string.IsNullOrWhiteSpace(_trade.RegimeTag) ? "—" : _trade.RegimeTag!;
            EdgeScoreText = _trade.EdgeScore > 0m ? _trade.EdgeScore.ToString("F1") : "—";
            EdgeBand = string.IsNullOrWhiteSpace(_trade.EdgeBand) ? "—" : _trade.EdgeBand!;
            SlippageText = $"{_trade.EntrySlippagePercent:F3}%";
            LatencyText = _trade.ExecutionLatencyMs > 0 ? $"{_trade.ExecutionLatencyMs} ms" : "—";
            SignalPriceText = _trade.SignalPrice > 0m ? _trade.SignalPrice.ToString("F6") : "—";
        }
        if (_signal is not null)
        {
            ConfidenceText = $"{_signal.Confidence:F0}/100";
            if (!string.IsNullOrWhiteSpace(_signal.Reasoning))
                ReasoningText = _signal.Reasoning!;
            if (StrategyTag == "—" && !string.IsNullOrWhiteSpace(_signal.StrategyTag))
                StrategyTag = _signal.StrategyTag!;
            if (RegimeTag == "—" && !string.IsNullOrWhiteSpace(_signal.RegimeTag))
                RegimeTag = _signal.RegimeTag!;
        }
    }

    [RelayCommand]
    public async Task LoadCandlesAsync()
    {
        IsLoading = true;
        StatusLine = $"Loading {Symbol} {Timeframe} candles…";
        Candles.Clear();
        OverlayLines.Clear();

        try
        {
            var candles = await _marketData.GetCandlesAsync(Symbol, 200, "Crypto", Timeframe).ConfigureAwait(true);
            if (candles is null || candles.Count == 0)
            {
                StatusLine = $"No candles returned for {Symbol} {Timeframe}.";
                return;
            }

            double priceMin = double.MaxValue, priceMax = double.MinValue;
            foreach (var c in candles)
            {
                priceMin = Math.Min(priceMin, (double)c.Low);
                priceMax = Math.Max(priceMax, (double)c.High);
            }
            // Include overlay price levels in the y-scale so the lines never fall off-screen.
            void IncludePrice(decimal v)
            {
                if (v <= 0m) return;
                priceMin = Math.Min(priceMin, (double)v);
                priceMax = Math.Max(priceMax, (double)v);
            }
            IncludePrice(_position.EntryPrice);
            IncludePrice(_position.StopLoss);
            IncludePrice(_position.TakeProfit);
            IncludePrice(_position.MarkPrice);

            var span = Math.Max(priceMax - priceMin, 1e-9);
            var pad = span * 0.05;
            priceMin -= pad; priceMax += pad; span = priceMax - priceMin;

            double bodyW = Math.Max(1, (ChartW / candles.Count) * 0.75);
            for (int i = 0; i < candles.Count; i++)
            {
                var c = candles[i];
                var x = ChartW * i / Math.Max(1, candles.Count - 1);
                double y(double p) => ChartH - (p - priceMin) / span * ChartH;

                var openY = y((double)c.Open);
                var closeY = y((double)c.Close);
                var bodyTop = Math.Min(openY, closeY);
                var bodyBottom = Math.Max(openY, closeY);
                var bullish = c.Close >= c.Open;

                Candles.Add(new Candle
                {
                    BodyX = x - bodyW / 2,
                    BodyY = bodyTop,
                    BodyWidth = Math.Max(1, bodyW),
                    BodyHeight = Math.Max(1, bodyBottom - bodyTop),
                    WickX = x,
                    WickY1 = y((double)c.High),
                    WickY2 = y((double)c.Low),
                    Fill = bullish ? Brushes.LimeGreen : Brushes.IndianRed
                });
            }

            OverlayLine Build(decimal price, Brush brush, string label, bool dashed = false)
            {
                if (price <= 0m) return new OverlayLine();
                double yy = ChartH - ((double)price - priceMin) / span * ChartH;
                var pc = new PointCollection { new Point(0, yy), new Point(ChartW, yy) };
                return new OverlayLine
                {
                    Points = pc,
                    Stroke = brush,
                    Label = $"{label} {price:F6}",
                    LabelY = yy - 10,
                    LabelX = ChartW - 120,
                    DashArray = dashed ? new DoubleCollection { 3, 3 } : null
                };
            }

            if (_position.EntryPrice > 0m)
                OverlayLines.Add(Build(_position.EntryPrice, Brushes.DeepSkyBlue, "ENTRY"));
            if (_position.StopLoss > 0m)
                OverlayLines.Add(Build(_position.StopLoss, Brushes.IndianRed, "SL", dashed: true));
            if (_position.TakeProfit > 0m)
                OverlayLines.Add(Build(_position.TakeProfit, Brushes.LimeGreen, "TP", dashed: true));
            if (_position.MarkPrice > 0m)
                OverlayLines.Add(Build(_position.MarkPrice, Brushes.Goldenrod, "MARK", dashed: true));

            StatusLine = $"Loaded {candles.Count} {Timeframe} bars. Entry/SL/TP/Mark overlaid.";
        }
        catch (Exception ex)
        {
            StatusLine = $"Candle load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
