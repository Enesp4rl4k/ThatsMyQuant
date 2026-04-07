using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Interfaces;

namespace ThatsMyQuant.Infrastructure.Services;

public class ConsoleAuditLogger : IAuditLogger
{
    private readonly ILogger<ConsoleAuditLogger> _logger;

    public ConsoleAuditLogger(ILogger<ConsoleAuditLogger> logger)
    {
        _logger = logger;
    }

    public Task LogSecurityEventAsync(string principalId, string role, string actionType, string details)
    {
        _logger.LogWarning(
            "SECURITY EVENT | Principal: {PrincipalId} | Role: {Role} | Action: {ActionType} | Details: {Details}",
            principalId, role, actionType, details);
        return Task.CompletedTask;
    }

    public Task LogOrderExecutionAsync(string orderId, string strategyId, string justification)
    {
        _logger.LogInformation(
            "ORDER EXECUTION | OrderId: {OrderId} | Strategy: {StrategyId} | Justification: {Justification}",
            orderId, strategyId, justification);
        return Task.CompletedTask;
    }
}
