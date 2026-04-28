using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public sealed class QuantLabAttributionRowViewModel
{
    public QuantLabAttributionRowViewModel(string strategy, decimal pnl1h, decimal pnl1d, decimal pnl1w, int totalTrades, Brush pnl1hBrush, Brush pnl1dBrush, Brush pnl1wBrush)
    {
        Strategy = strategy;
        Pnl1h = pnl1h;
        Pnl1d = pnl1d;
        Pnl1w = pnl1w;
        TotalTrades = totalTrades;
        Pnl1hBrush = pnl1hBrush;
        Pnl1dBrush = pnl1dBrush;
        Pnl1wBrush = pnl1wBrush;
    }

    public string Strategy { get; }
    public decimal Pnl1h { get; }
    public decimal Pnl1d { get; }
    public decimal Pnl1w { get; }
    public int TotalTrades { get; }
    public Brush Pnl1hBrush { get; }
    public Brush Pnl1dBrush { get; }
    public Brush Pnl1wBrush { get; }
}
