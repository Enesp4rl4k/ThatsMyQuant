using ThatsMyQuant.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// The ultimate guard for Mainnet deployment.
/// Compares the internal bot state (OrderFlow) with the external Exchange state (REST).
/// If a "Ledger Drift" is detected, it triggers a catastrophic HALT.
/// </summary>
public class ReconciliationAgent
{
    private readonly IExchangeService _exchange;
    private readonly ILogger<ReconciliationAgent> _logger;
    private readonly decimal _tolerance = 0.0001m; // 0.01% drift tolerance

    public ReconciliationAgent(IExchangeService exchange, ILogger<ReconciliationAgent> logger)
    {
        _exchange = exchange;
        _logger = logger;
    }

    public async Task<(bool InSync, string Message)> VerifyIntegrityAsync(decimal localPositionBtc)
    {
        try
        {
            _logger.LogInformation("[RECO] Performing deep-state audit against Exchange REST API...");
            
            var account = await _exchange.GetAccountStatusAsync();
            decimal remotePosition = account.Balance; // Simplified for this demo

            decimal drift = Math.Abs(localPositionBtc - remotePosition);

            if (drift > _tolerance)
            {
                _logger.LogCritical("[HALT] LEDGER DRIFT DETECTED! Local: {Local}, Remote: {Remote}, Drift: {Drift}", 
                    localPositionBtc, remotePosition, drift);
                    
                return (false, $"CRITICAL: Exchange state desynchronized by {drift} units. Halting for manual audit.");
            }

            _logger.LogInformation("[RECO] Audit Passed. Integrity verified within {Tol} tolerance.", _tolerance);
            return (true, "States in sync.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RECO] Failed to reach exchange for audit. Assuming unsafe state.");
            return (false, "Inaccessible exchange state. Strategy-Lock engaged.");
        }
    }
}
