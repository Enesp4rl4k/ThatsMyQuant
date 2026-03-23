namespace ThatsMyQuant.Domain.Interfaces;

/// <summary>
/// Enterprise contract used to securely validate Exchange-specific instrument attributes.
/// Replaces heuristic mapping with exact hardware double bounds.
/// </summary>
public interface IExchangeInfoProvider
{
    double GetTickSize(string symbol);
    double GetLotSize(string symbol);
}
