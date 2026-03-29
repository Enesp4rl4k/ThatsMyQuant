using ThatsMyQuant.Contracts.Models;

namespace ThatsMyQuant.Application.Strategy;

/// <summary>
/// Computes leading microstructure indicators, primarily Net Order Flow Imbalance (OFI),
/// from high-frequency Level-2 Order Book updates.
/// </summary>
public static class OrderFlowImbalanceAnalyzer
{
    /// <summary>
    /// Calculates the Net Order Flow Imbalance from two sequential L2 snapshots.
    /// Positive OFI suggests short-term upward price pressure (buy pressure overpowers).
    /// Negative OFI suggests short-term downward price pressure (sell pressure overpowers).
    /// </summary>
    public static decimal ComputeOFI(OrderBookSnapshot previous, OrderBookSnapshot current)
    {
        if (previous.Bids.Length == 0 || previous.Asks.Length == 0 ||
            current.Bids.Length == 0 || current.Asks.Length == 0)
        {
            return 0m;
        }

        var prevBid = previous.Bids[0];
        var currBid = current.Bids[0];
        
        var prevAsk = previous.Asks[0];
        var currAsk = current.Asks[0];

        // Bid Imbalance
        decimal bidImbalance;
        if (currBid.Price > prevBid.Price)
            bidImbalance = currBid.Quantity; // Bids shifted up, all new volume is aggressive
        else if (currBid.Price == prevBid.Price)
            bidImbalance = currBid.Quantity - prevBid.Quantity; // Change in depth at same price
        else
            bidImbalance = -prevBid.Quantity; // Bids shifted down, old volume vanished

        // Ask Imbalance
        decimal askImbalance;
        if (currAsk.Price < prevAsk.Price)
            askImbalance = currAsk.Quantity; // Asks shifted down, all new volume is aggressive
        else if (currAsk.Price == prevAsk.Price)
            askImbalance = currAsk.Quantity - prevAsk.Quantity; // Change in depth at same price
        else
            askImbalance = -prevAsk.Quantity; // Asks shifted up, old volume vanished

        // Net OFI
        return bidImbalance - askImbalance;
    }
}
