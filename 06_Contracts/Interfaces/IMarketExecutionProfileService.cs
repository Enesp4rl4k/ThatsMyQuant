using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Interfaces;

public interface IMarketExecutionProfileService
{
    decimal ResolveSlippagePercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null);
    decimal ResolveSpreadPercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null);
    decimal ResolveCommissionPercent(string symbol, IDictionary<string, string>? metadata, decimal? overridePercent = null);
    decimal ApplyExecutionPrice(decimal price, TradeSide side, bool isEntry, string symbol,
        IDictionary<string, string>? metadata = null, decimal? overrideSlippagePercent = null,
        decimal? overrideSpreadPercent = null, Candle? candle = null);
    decimal CalculateNetPnl(decimal entryPrice, decimal exitPrice, decimal quantity, TradeSide side,
        string symbol, IDictionary<string, string>? metadata = null, decimal? overrideCommissionPercent = null);
}
