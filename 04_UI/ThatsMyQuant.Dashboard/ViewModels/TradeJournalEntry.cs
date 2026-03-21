using System;
using System.Windows.Media;

namespace ThatsMyQuant.Dashboard.ViewModels;

public class TradeJournalEntry
{
    public DateTime Time { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Price { get; set; }
    public int Confidence { get; set; }
    public string Status { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string ExecutionMode { get; set; } = "";
    public Brush ExecutionModeColor { get; set; } = Brushes.Gray;
    public Brush SideColor { get; set; } = Brushes.Gray;
    public decimal PnL { get; set; }
}
