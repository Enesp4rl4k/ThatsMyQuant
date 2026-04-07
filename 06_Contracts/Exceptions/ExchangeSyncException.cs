namespace ThatsMyQuant.Contracts.Exceptions;

public sealed class ExchangeSyncException : Exception
{
    public string ExchangeName { get; }
    public string Detail { get; }

    public ExchangeSyncException(string exchangeName, string detail)
        : base($"[{exchangeName}] {detail}")
    {
        ExchangeName = exchangeName;
        Detail = detail;
    }
}
