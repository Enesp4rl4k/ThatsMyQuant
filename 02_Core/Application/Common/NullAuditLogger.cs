using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Application.Common;

/// <summary>
/// A no-op implementation of IAuditLogger to prevent DI failures when Infrastructure is not registered.
/// </summary>
public sealed class NullAuditLogger : IAuditLogger
{
    public Task LogSecurityEventAsync(string user, string action, string resource, string details) 
        => Task.CompletedTask;

    public Task LogTradeEventAsync(Guid tradeId, string action, string details) 
        => Task.CompletedTask;

    public Task LogSystemAlertAsync(string level, string component, string message) 
        => Task.CompletedTask;
}
