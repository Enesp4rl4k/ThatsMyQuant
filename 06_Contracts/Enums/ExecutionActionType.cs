namespace ThatsMyQuant.Contracts.Enums;

/// <summary>
/// Strongly-typed execution targets replacing raw dictionary strings.
/// </summary>
public enum ExecutionActionType
{
    None = 0,
    LimitFrontRunBuy = 1,
    LimitFrontRunSell = 2,
    MarketTaker = 3,
    Cancellation = 4
}
