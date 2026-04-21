using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Common;

/// <summary>
/// A no-op implementation of IAuditLogger to prevent DI failures when Infrastructure is not registered.
/// </summary>
public sealed class NullAuditLogger : IAuditLogger
{
    public Task LogSecurityEventAsync(string principalId, string role, string actionType, string details) 
        => Task.CompletedTask;

    public Task LogOrderExecutionAsync(string orderId, string strategyId, string justification) 
        => Task.CompletedTask;
}
