namespace ThatsMyQuant.Contracts.Models;

public class Account
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty; // e.g., "Binance", "PropFirm"
    public decimal Balance { get; set; }
    public decimal Equity { get; set; }
    public List<Position> Positions { get; set; } = new();
}
