using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Contracts.Helpers;

public static class ExecutionOrderIdentity
{
    public const string ClientOrderIdKey = "ClientOrderId";
    public const string IdempotencyKey = "IdempotencyKey";

    public static string CreateClientOrderId(Guid signalId)
        => signalId.ToString("N");

    public static string EnsureClientOrderId(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (signal.Metadata.TryGetValue(ClientOrderIdKey, out var existing) &&
            TryParseSignalId(existing, out _))
        {
            signal.Metadata[ClientOrderIdKey] = existing;
            signal.Metadata[IdempotencyKey] = existing;
            return existing;
        }

        var clientOrderId = CreateClientOrderId(signal.Id);
        signal.Metadata[ClientOrderIdKey] = clientOrderId;
        signal.Metadata[IdempotencyKey] = clientOrderId;
        return clientOrderId;
    }

    public static bool TryParseSignalId(string? clientOrderId, out Guid signalId)
    {
        if (Guid.TryParseExact(clientOrderId, "N", out signalId))
        {
            return true;
        }

        return Guid.TryParse(clientOrderId, out signalId);
    }
}
