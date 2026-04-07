using ThatsMyQuant.Contracts.Enums;

namespace ThatsMyQuant.Contracts.Models;

public sealed record OpenOrderSnapshot(
    string Symbol,
    TradeSide Side,
    string ClientOrderId,
    string ExchangeOrderId,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    decimal Price,
    decimal AveragePrice,
    string Status,
    DateTime TimestampUtc);
