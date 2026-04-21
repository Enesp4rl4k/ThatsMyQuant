using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using ThatsMyQuant.Contracts.Commands;
using ThatsMyQuant.Contracts.Enums;
using ThatsMyQuant.Contracts.Interfaces;
using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// A Delta-Neutral Hedging engine (Cash and Carry Arbitrage).
/// Monitors extreme funding rates on Perpetual Swaps and executes a risk-free yield capture
/// by going Long Spot and Short Perpetual. Captures the funding interval safely.
/// </summary>
public class FundingArbitrageAgent : INotificationHandler<Signal>
{
    private readonly ILogger<FundingArbitrageAgent> _logger;
    private readonly IMediator _mediator;
    private readonly IFundingRateProvider _fundingRateProvider;

    // Minimum yield required to initiate a hedge (e.g., 0.03% = 3 bps per 8h = 32% APY approx)
    private const decimal EntryThresholdBps = 3m; 
    private const decimal ExitThresholdBps = 0.5m;

    private readonly HashSet<string> _activeHedges = new();

    public FundingArbitrageAgent(
        ILogger<FundingArbitrageAgent> logger, 
        IMediator mediator,
        IFundingRateProvider? fundingRateProvider = null)
    {
        _logger = logger;
        _mediator = mediator;
        _fundingRateProvider = fundingRateProvider ?? new NullFundingRateProvider();
    }

    public async Task Handle(Signal notification, CancellationToken cancellationToken)
    {
        // Rate limiting: we only check funding once per distinct price tick roughly.
        // In real environments, this connects to a WebSocket Funding Rate stream instead of Price stream.
        await EvaluateFundingRatesAsync(notification.Symbol, notification.Price, cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateFundingRatesAsync(string baseSymbol, decimal currentSpotPrice, CancellationToken ct)
    {
        // Strip out any trailing market IDs to get pure asset, e.g. "BTC-USDT"
        if (baseSymbol.Contains("-SWAP")) return; 

        string pertSymbol = $"{baseSymbol}-SWAP";
        
        // Retrieve real-time funding rate from the provider (needs latest stamp)
        decimal rawFundingRate = _fundingRateProvider.GetRate(pertSymbol, DateTime.UtcNow);
        
        if (rawFundingRate == 0) return;

        decimal fundingRateBps = rawFundingRate * 10_000m; // Convert to basis points

        if (!_activeHedges.Contains(baseSymbol) && fundingRateBps >= EntryThresholdBps)
        {
            _logger.LogInformation(
                "FUNDING ARB SIGNAL: Extreme positive funding rate detected on {PerpSymbol} ({Rate} bps). Initiating Cash and Carry.", 
                pertSymbol, fundingRateBps);

            _activeHedges.Add(baseSymbol);
            await ExecuteCashAndCarryAsync(baseSymbol, pertSymbol, currentSpotPrice, TradeSide.Sell, ct).ConfigureAwait(false);
            // TradeSide.Sell implies shorting the perp and entering long on Spot
        }
        else if (_activeHedges.Contains(baseSymbol) && fundingRateBps <= ExitThresholdBps)
        {
            _logger.LogInformation(
                "FUNDING ARB SIGNAL: Funding rate on {PerpSymbol} cooled off ({Rate} bps). Exiting hedge.", 
                pertSymbol, fundingRateBps);

            _activeHedges.Remove(baseSymbol);
            await ExecuteCashAndCarryAsync(baseSymbol, pertSymbol, currentSpotPrice, TradeSide.Buy, ct).ConfigureAwait(false);
            // Closing: Longing the perp and Shorting (selling) the Spot
        }
    }

    private async Task ExecuteCashAndCarryAsync(string spotSymbol, string perpSymbol, decimal price, TradeSide perpSide, CancellationToken ct)
    {
        // Delta Neutral calculation: Long Spot == Short Perp
        TradeSide spotSide = perpSide == TradeSide.Sell ? TradeSide.Buy : TradeSide.Sell;

        var spotLeg = new Signal
        {
            Symbol = spotSymbol,
            Exchange = "OKX",
            Side = spotSide,
            Price = price,
            StrategyTag = "FundingArbitrage",
            Confidence = 99,
            Reasoning = "Cash & Carry delta-neutral Spot Leg",
            Quantity = 1000m / price, // Base 1000 USDT allocation
            Metadata = new Dictionary<string, string> { { "ExecutionMode", "DeltaNeutral" } }
        };

        var perpLeg = new Signal
        {
            Symbol = perpSymbol,
            Exchange = "OKX",
            Side = perpSide,
            Price = price, // Approx same price
            StrategyTag = "FundingArbitrage",
            Confidence = 99,
            Reasoning = "Cash & Carry delta-neutral Perpetual Leg",
            Quantity = 1000m / price,
            Metadata = new Dictionary<string, string> { { "ExecutionMode", "DeltaNeutral" } }
        };

        await Task.WhenAll(
            _mediator.Send(new ExecuteTradeCommand(spotLeg), ct),
            _mediator.Send(new ExecuteTradeCommand(perpLeg), ct)).ConfigureAwait(false);
    }
}
