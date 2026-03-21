using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Domain.Entities;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A specialized execution agent that monitors Order Flow Imbalance (OFI).
/// When a massive 'Buy Wall' is detected in the order book, it places a limit order 
/// exactly 1 tick ahead of the wall to ensure it is filled before the price 'pumps'.
/// </summary>
public class OFIExecutionAgent : INotificationHandler<Signal>
{
    private readonly ILogger<OFIExecutionAgent> _logger;

    public OFIExecutionAgent(ILogger<OFIExecutionAgent> logger)
    {
        _logger = logger;
    }

    public Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // Simulate extraction of OFI from Signal Metadata (calculated by MicrosecondPredictionEngine)
        if (notification.Metadata.TryGetValue("OrderFlowImbalance", out var ofiStr) && 
            float.TryParse(ofiStr, out var imbalance))
        {
            // If Buy-side imbalance is massive (> 0.8), front-run the wall.
            if (imbalance > 0.8f)
            {
                decimal frontRunPrice = notification.Price + 0.01m; // 1 tick front-run
                
                _logger.LogInformation($"[FRONT-RUN] Massive Buy Wall detected ({imbalance:P0}). Placing defensive Limit Buy at {frontRunPrice:F2} ahead of market makers.");
                
                notification.Metadata["Execution_Action"] = "Limit_FrontRun_Buy";
                notification.Reasoning += $" | OFI Front-Run: {imbalance:P0}";
            }
            // If Sell-side imbalance is massive (< -0.8), defensive limit sell.
            else if (imbalance < -0.8f)
            {
                decimal frontRunPrice = notification.Price - 0.01m;
                
                _logger.LogInformation($"[FRONT-RUN] Massive Sell Wall detected ({Math.Abs(imbalance):P0}). Placing defensive Limit Sell at {frontRunPrice:F2}.");
                
                notification.Metadata["Execution_Action"] = "Limit_FrontRun_Sell";
            }
        }

        return Task.CompletedTask;
    }
}
