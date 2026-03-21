using System;

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
}
