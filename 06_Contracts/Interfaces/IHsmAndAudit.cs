namespace ThatsMyQuant.Contracts.Interfaces;

/// <summary>
/// Virtual representation of an HSM (Hardware Security Module) meant for vaulting cryptographic material 
/// and securely signing raw exchange transactions via API keys, preventing memory-scraping vulnerabilities.
/// </summary>
public interface IHardwareSecurityModule
{
    /// <summary>
    /// Computes an HMAC or ECDSA payload signature using vaulted keys without ever returning the private key to the caller.
    /// </summary>
    Task<string> SignPayloadAsync(string exchangeId, string payload);
}

/// <summary>
/// Institutional log tracing for regulatory and compliance audits (e.g., SEC/FINRA requirements).
/// Immutably records exactly which 'Role' (from RBAC) initiated a system parameter change or order.
/// </summary>
public interface IAuditLogger
{
    Task LogSecurityEventAsync(string principalId, string role, string actionType, string details);
    Task LogOrderExecutionAsync(string orderId, string strategyId, string justification);
}
