namespace ThatsMyQuant.Domain.Enums;

public enum TradeStatus
{
    Pending,
    Queued,
    Executed,
    Rejected,
    Failed,
    DeadLetter
}
