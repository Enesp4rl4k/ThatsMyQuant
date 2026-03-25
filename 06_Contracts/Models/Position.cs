namespace ThatsMyQuant.Contracts.Models;

public class Position
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
}
