using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels.SubViewModels;

public sealed class QuantLabFoldBarViewModel
{
    public QuantLabFoldBarViewModel(string label, double sharpeBarHeight, double winRateBarHeight, double sharpe, double winRate, int tradeCount, Brush sharpeBrush, Brush winRateBrush)
    {
        Label = label;
        SharpeBarHeight = sharpeBarHeight;
        WinRateBarHeight = winRateBarHeight;
        Sharpe = sharpe;
        WinRate = winRate;
        TradeCount = tradeCount;
        SharpeBrush = sharpeBrush;
        WinRateBrush = winRateBrush;
        Tooltip = $"{label} · Sharpe {sharpe:F2} · WR {winRate:P0} · {tradeCount} trades";
    }

    public string Label { get; }
    public double SharpeBarHeight { get; }
    public double WinRateBarHeight { get; }
    public double Sharpe { get; }
    public double WinRate { get; }
    public int TradeCount { get; }
    public Brush SharpeBrush { get; }
    public Brush WinRateBrush { get; }
    public string Tooltip { get; }
}
